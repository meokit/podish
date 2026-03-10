using System.Diagnostics;
using System.Globalization;

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
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        if (!TryRunUnixStat(hostPath, out var statParts)) return false;
        if (statParts.Length < 4) return false;

        if (!ulong.TryParse(statParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dev))
            return false;
        if (!ulong.TryParse(statParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ino))
            return false;
        if (!int.TryParse(statParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nlink))
            return false;
        if (nlink < 0) nlink = 0;

        uint rawMode;
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                rawMode = Convert.ToUInt32(statParts[3], 8);
            }
            catch
            {
                return false;
            }
        }
        else
        {
            if (!uint.TryParse(statParts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rawMode))
                return false;
        }

        var rawType = DecodeUnixRawType(rawMode);
        node = new HostNodeInfo(HostInodeKey.Unix(dev, ino), rawType, nlink, dev);
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
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

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

    private static bool TryRunUnixStat(string hostPath, out string[] parts)
    {
        parts = [];
        var statPath = OperatingSystem.IsMacOS() ? "/usr/bin/stat" : "stat";
        var psi = new ProcessStartInfo
        {
            FileName = statPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsMacOS())
        {
            // dev|ino|nlink|mode(octal)
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%d|%i|%l|%p");
        }
        else
        {
            // dev|ino|nlink|mode(hex)
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%d|%i|%h|%f");
        }

        psi.ArgumentList.Add(hostPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
                return false;
            parts = output.Split('|', StringSplitOptions.TrimEntries);
            return parts.Length >= 4;
        }
        catch
        {
            return false;
        }
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
}
