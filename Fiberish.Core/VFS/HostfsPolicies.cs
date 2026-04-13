using System.Runtime.InteropServices;

namespace Fiberish.VFS;

public enum HostfsMountBoundaryMode
{
    SingleDomain = 0,
    Passthrough = 1
}

public enum HostfsSpecialNodeMode
{
    Strict = 0,
    Passthrough = 1
}

internal readonly record struct HostNodeInfo(
    HostInodeKey Identity,
    InodeType RawType,
    int? HostLinkCount,
    ulong? MountDomainId);

public interface IMountBoundaryPolicy
{
    bool Allows(string mountRootPath, string candidatePath, ulong? rootMountDomainId, ulong? candidateMountDomainId);
}

public interface ISpecialNodePolicy
{
    bool TryMapType(InodeType rawType, out InodeType type);
}

public sealed class SingleDomainMountBoundaryPolicy : IMountBoundaryPolicy
{
    public bool Allows(string _, string __, ulong? rootMountDomainId, ulong? candidateMountDomainId)
    {
        if (!rootMountDomainId.HasValue || !candidateMountDomainId.HasValue)
            return true;
        return rootMountDomainId.Value == candidateMountDomainId.Value;
    }
}

public sealed class PassthroughMountBoundaryPolicy : IMountBoundaryPolicy
{
    public bool Allows(string _, string __, ulong? rootMountDomainId, ulong? candidateMountDomainId)
    {
        return true;
    }
}

public sealed class StrictSpecialNodePolicy : ISpecialNodePolicy
{
    public bool TryMapType(InodeType rawType, out InodeType type)
    {
        switch (rawType)
        {
            case InodeType.File:
            case InodeType.Directory:
            case InodeType.Symlink:
                type = rawType;
                return true;
            default:
                type = InodeType.Unknown;
                return false;
        }
    }
}

public sealed class PassthroughSpecialNodePolicy : ISpecialNodePolicy
{
    public bool TryMapType(InodeType rawType, out InodeType type)
    {
        if (rawType == InodeType.Unknown)
        {
            type = InodeType.Unknown;
            return false;
        }

        type = rawType;
        return true;
    }
}

internal static partial class HostInodeIdentityResolver
{
    private const uint UnixModeTypeMask = 0xF000;
    private const uint UnixModeFifo = 0x1000;
    private const uint UnixModeCharDevice = 0x2000;
    private const uint UnixModeDirectory = 0x4000;
    private const uint UnixModeBlockDevice = 0x6000;
    private const uint UnixModeRegular = 0x8000;
    private const uint UnixModeSymlink = 0xA000;
    private const uint UnixModeSocket = 0xC000;
#if !HOSTFS_DARWIN && !HOSTFS_LINUX && !HOSTFS_WINDOWS
    private static readonly bool IsDarwinPlatform = OperatingSystem.IsMacOS() ||
                                                    OperatingSystem.IsIOS() ||
                                                    OperatingSystem.IsTvOS() ||
                                                    OperatingSystem.IsWatchOS() ||
                                                    OperatingSystem.IsMacCatalyst();
#endif

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_linux_x64(string path, out LinuxStatX64 stat);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_linux_arm64(string path, out LinuxStatArm64 stat);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_darwin(string path, out DarwinStat stat);

    public static bool TryProbe(string hostPath, out HostNodeInfo node)
    {
        var normalizedPath = Path.GetFullPath(hostPath);
        if (TryProbeUnixNode(normalizedPath, out node))
            return true;
        if (TryProbeWindowsNode(normalizedPath, out node))
            return true;
        if (TryProbePortable(normalizedPath, out node))
            return true;

        node = default;
        return false;
    }

    private static bool TryProbeUnixNode(string hostPath, out HostNodeInfo node)
    {
        node = default;
        if (!TryReadUnixStat(hostPath, out var statData)) return false;
        var nlink = statData.LinkCount > int.MaxValue ? int.MaxValue : (int)statData.LinkCount;
        var rawType = DecodeUnixRawType(statData.Mode);
        node = new HostNodeInfo(HostInodeKey.Unix(statData.Device, statData.Inode), rawType, nlink, statData.Device);
        return rawType != InodeType.Unknown;
    }

    private static bool TryProbeWindowsNode(string hostPath, out HostNodeInfo node)
    {
        node = default;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var attributes = File.GetAttributes(hostPath);
            var info = new FileInfo(hostPath);
            InodeType rawType;
            if (info.LinkTarget != null)
                rawType = InodeType.Symlink;
            else if ((attributes & FileAttributes.Directory) != 0)
                rawType = InodeType.Directory;
            else if ((attributes & FileAttributes.ReparsePoint) != 0)
                rawType = InodeType.Unknown;
            else
                rawType = InodeType.File;

            using var handle = File.OpenHandle(
                hostPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(handle, out var fileInfo))
                return false;

            var fileId = ((ulong)fileInfo.NFileIndexHigh << 32) | fileInfo.NFileIndexLow;
            var nlink = checked((int)fileInfo.NNumberOfLinks);
            if (nlink < 0) nlink = 0;
            node = new HostNodeInfo(
                HostInodeKey.Windows(fileInfo.DwVolumeSerialNumber, fileId),
                rawType,
                nlink,
                fileInfo.DwVolumeSerialNumber);
            return rawType != InodeType.Unknown;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryProbePortable(string hostPath, out HostNodeInfo node)
    {
        node = default;
        try
        {
            var info = new FileInfo(hostPath);
            var rawType = info.LinkTarget != null
                ? InodeType.Symlink
                : Directory.Exists(hostPath)
                    ? InodeType.Directory
                    : File.Exists(hostPath)
                        ? InodeType.File
                        : InodeType.Unknown;
            if (rawType == InodeType.Unknown)
                return false;

            node = new HostNodeInfo(
                HostInodeKey.Fallback(hostPath),
                rawType,
                null,
                null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadUnixStat(string hostPath, out HostUnixStatData statData)
    {
        statData = default;
#if HOSTFS_LINUX
#if HOSTFS_ARCH_X64
        if (lstat_linux_x64(hostPath, out var linuxX64Stat) != 0) return false;
        statData = new HostUnixStatData(
            linuxX64Stat.StDev, linuxX64Stat.StIno, linuxX64Stat.StNlink, linuxX64Stat.StMode, linuxX64Stat.StUid, linuxX64Stat.StGid);
        return true;
#elif HOSTFS_ARCH_ARM64
        if (lstat_linux_arm64(hostPath, out var linuxArm64Stat) != 0) return false;
        statData = new HostUnixStatData(
            linuxArm64Stat.StDev, linuxArm64Stat.StIno, linuxArm64Stat.StNlink, linuxArm64Stat.StMode, linuxArm64Stat.StUid, linuxArm64Stat.StGid);
        return true;
#else
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            if (lstat_linux_x64(hostPath, out var st) != 0) return false;
            statData = new HostUnixStatData(st.StDev, st.StIno, st.StNlink, st.StMode, st.StUid, st.StGid);
            return true;
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (lstat_linux_arm64(hostPath, out var st) != 0) return false;
            statData = new HostUnixStatData(st.StDev, st.StIno, st.StNlink, st.StMode, st.StUid, st.StGid);
            return true;
        }

        return false;
#endif
#elif HOSTFS_DARWIN
        if (lstat_darwin(hostPath, out var darwinStat) != 0)
            return false;

        statData = new HostUnixStatData(
            unchecked((ulong)(uint)darwinStat.StDev),
            darwinStat.StIno,
            darwinStat.StNlink,
            darwinStat.StMode,
            darwinStat.StUid,
            darwinStat.StGid,
            darwinStat.StSize);
        return true;
#elif HOSTFS_WINDOWS
        return false;
#else
        if (OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                if (lstat_linux_x64(hostPath, out var st) != 0) return false;
                statData = new HostUnixStatData(st.StDev, st.StIno, st.StNlink, st.StMode, st.StUid, st.StGid,
                    st.StSize);
                return true;
            }

            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (lstat_linux_arm64(hostPath, out var st) != 0) return false;
                statData = new HostUnixStatData(st.StDev, st.StIno, st.StNlink, st.StMode, st.StUid, st.StGid,
                    st.StSize);
                return true;
            }

            return false;
        }

        if (!IsDarwinPlatform)
            return false;

        if (lstat_darwin(hostPath, out var fallbackDarwinStat) != 0)
            return false;

        statData = new HostUnixStatData(
            unchecked((uint)fallbackDarwinStat.StDev),
            fallbackDarwinStat.StIno,
            fallbackDarwinStat.StNlink,
            fallbackDarwinStat.StMode,
            fallbackDarwinStat.StUid,
            fallbackDarwinStat.StGid,
            fallbackDarwinStat.StSize);
        return true;
#endif
    }

    private static InodeType DecodeUnixRawType(uint rawMode)
    {
        return (rawMode & UnixModeTypeMask) switch
        {
            UnixModeRegular => InodeType.File,
            UnixModeDirectory => InodeType.Directory,
            UnixModeSymlink => InodeType.Symlink,
            UnixModeCharDevice => InodeType.CharDev,
            UnixModeBlockDevice => InodeType.BlockDev,
            UnixModeFifo => InodeType.Fifo,
            UnixModeSocket => InodeType.Socket,
            _ => InodeType.Unknown
        };
    }

    internal readonly record struct HostUnixStatData(
        ulong Device,
        ulong Inode,
        ulong LinkCount,
        uint Mode,
        uint Uid,
        uint Gid,
        long SizeBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec
    {
        public long TvSec;
        public long TvNsec;
    }

    // glibc struct stat layout on Linux x86_64.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatX64
    {
        public ulong StDev;
        public ulong StIno;
        public ulong StNlink;
        public uint StMode;
        public uint StUid;
        public uint StGid;
        public int Padding0;
        public ulong StRdev;
        public long StSize;
        public long StBlksize;
        public long StBlocks;
        public LinuxTimespec StAtim;
        public LinuxTimespec StMtim;
        public LinuxTimespec StCtim;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    // glibc struct stat layout on Linux arm64.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatArm64
    {
        public ulong StDev;
        public ulong StIno;
        public uint StMode;
        public uint StNlink;
        public uint StUid;
        public uint StGid;
        public ulong StRdev;
        public ulong Padding1;
        public long StSize;
        public int StBlksize;
        public int Padding2;
        public long StBlocks;
        public LinuxTimespec StAtim;
        public LinuxTimespec StMtim;
        public LinuxTimespec StCtim;
        public uint Reserved0;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinTimespec
    {
        public long TvSec;
        public long TvNsec;
    }

    // Darwin (macOS/iOS/tvOS/watchOS/MacCatalyst) struct stat layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int StDev;
        public ushort StMode;
        public ushort StNlink;
        public ulong StIno;
        public uint StUid;
        public uint StGid;
        public int StRdev;
        public DarwinTimespec StAtimespec;
        public DarwinTimespec StMtimespec;
        public DarwinTimespec StCtimespec;
        public DarwinTimespec StBirthtimespec;
        public long StSize;
        public long StBlocks;
        public int StBlksize;
        public uint StFlags;
        public uint StGen;
        public int StLspare;
        public long StQspare0;
        public long StQspare1;
    }
}