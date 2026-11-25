using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

/// <summary>
///     Represents a System V shared memory segment.
/// </summary>
public class SysVShmSegment
{
    public int Key { get; set; } // IPC key (IPC_PRIVATE = 0)
    public int Shmid { get; set; } // Segment identifier
    public uint Size { get; set; } // Requested size in bytes
    public int Mode { get; set; } // Permission mode (lower 9 bits)
    public int Uid { get; set; } // Owner UID
    public int Gid { get; set; } // Owner GID
    public int CUid { get; set; } // Creator UID
    public int CGid { get; set; } // Creator GID
    public DateTime CTime { get; set; } // Create time
    public DateTime ATime { get; set; } // Last attach time
    public DateTime DTime { get; set; } // Last detach time
    public int Cpid { get; set; } // Creator PID
    public int Lpid { get; set; } // Last operator PID
    public int NAttch { get; set; } // Attach count
    public bool MarkedForDelete { get; set; }
    public LinuxFile BackingFile { get; set; } = null!;
    public AddressSpace? BackingObject => BackingFile.OpenedInode?.Mapping;

    /// <summary>
    ///     Number of pages in this segment.
    /// </summary>
    public uint PageCount => (uint)(((ulong)Size + LinuxConstants.PageSize - 1) / LinuxConstants.PageSize);
    internal uint MappedSize => PageCount * LinuxConstants.PageSize;
}

/// <summary>
///     Represents an attachment of a process to a shared memory segment.
/// </summary>
public class SysVShmAttach
{
    public int Pid { get; set; } // Process that attached
    public uint BaseAddr { get; set; } // Virtual address where attached
    public int Shmid { get; set; } // Segment ID
    public Protection Prot { get; set; } // Mapping protection
    public VMAManager AddressSpace { get; set; } = null!; // Address space that owns this attach
}

/// <summary>
///     Manages System V shared memory segments globally.
///     This is a global IPC namespace shared across all processes in the emulator.
/// </summary>
public class SysVShmManager
{
    private readonly List<SysVShmAttach> _attaches = new();
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly Dictionary<int, SysVShmSegment> _segmentsByKey = new();
    private readonly Dictionary<int, SysVShmSegment> _segmentsByShmid = new();
    private int _nextShmid = 1;

    internal SysVShmManager(MemoryRuntimeContext memoryContext, DeviceNumberManager? deviceNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        _memoryContext = memoryContext;
        _memoryContext.GetOrCreateShmSuperBlock(deviceNumbers);
    }

    /// <summary>
    ///     Get or create a shared memory segment.
    /// </summary>
    /// <param name="key">IPC key (0 = IPC_PRIVATE)</param>
    /// <param name="size">Requested size in bytes</param>
    /// <param name="flags">IPC_CREAT, IPC_EXCL, and permission flags</param>
    /// <param name="uid">Creator/owner UID</param>
    /// <param name="gid">Creator/owner GID</param>
    /// <param name="pid">Creator PID</param>
    /// <returns>shmid on success, negative errno on failure</returns>
    public int ShmGet(int key, uint size, int flags, int uid, int gid, int pid)
    {
        // Single-container scheduler-thread ownership: SysV SHM metadata is mutated on scheduler thread.
        // Check if key exists (and not IPC_PRIVATE)
        if (key != LinuxConstants.IPC_PRIVATE && _segmentsByKey.TryGetValue(key, out var existing))
        {
            // Key exists - IPC_EXCL only causes EEXIST when combined with IPC_CREAT
            if ((flags & LinuxConstants.IPC_CREAT) != 0 && (flags & LinuxConstants.IPC_EXCL) != 0)
                return -(int)Errno.EEXIST; // IPC_CREAT | IPC_EXCL and exists -> error

            // Check size (must be <= existing size for existing segment)
            if (size > existing.Size)
                return -(int)Errno.EINVAL;

            // Check permissions
            if (!CheckPermission(existing, uid, gid, flags))
                return -(int)Errno.EACCES;

            return existing.Shmid;
        }

        // Key doesn't exist or is IPC_PRIVATE
        if (key != LinuxConstants.IPC_PRIVATE && (flags & LinuxConstants.IPC_CREAT) == 0)
            return -(int)Errno.ENOENT; // No IPC_CREAT and doesn't exist -> error

        var alignedSize = ((ulong)size + LinuxConstants.PageSize - 1) & ~(ulong)(LinuxConstants.PageSize - 1);
        if (size == 0 || alignedSize > uint.MaxValue)
            return -(int)Errno.EINVAL;

        // Create new segment
        var shmid = _nextShmid++;
        SysVShmSegment segment;
        try
        {
            segment = new SysVShmSegment
            {
                Key = key,
                Shmid = shmid,
                Size = size,
                Mode = flags & 0x1FF, // Lower 9 bits are permissions
                Uid = uid,
                Gid = gid,
                CUid = uid,
                CGid = gid,
                Cpid = pid,
                CTime = DateTime.UtcNow,
                ATime = DateTime.UnixEpoch,
                DTime = DateTime.UnixEpoch,
                NAttch = 0,
                MarkedForDelete = false,
                BackingFile = _memoryContext.CreateSysVSharedMemoryFile(shmid, (uint)alignedSize, uid, gid)
            };
        }
        catch
        {
            return -(int)Errno.ENOMEM;
        }

        _segmentsByShmid[shmid] = segment;
        if (key != LinuxConstants.IPC_PRIVATE)
            _segmentsByKey[key] = segment;

        return shmid;
    }

    /// <summary>
    ///     Attach a shared memory segment to the process address space.
    /// </summary>
    /// <param name="shmid">Segment ID</param>
    /// <param name="addr">Desired address (0 = auto-allocate)</param>
    /// <param name="flags">SHM_RDONLY, SHM_RND, SHM_REMAP</param>
    /// <param name="pid">Process ID</param>
    /// <param name="vmaManager">VmArea manager for the process</param>
    /// <param name="engine">Engine instance</param>
    /// <returns>Attached address on success, negative errno on failure</returns>
    public long ShmAt(int shmid, uint addr, int flags, int pid, VMAManager vmaManager, Engine engine,
        Process? process = null)
    {
        var ownerProcess = process ?? (engine.Owner as FiberTask)?.Process;
        SysVShmSegment segment;
        if (!_segmentsByShmid.TryGetValue(shmid, out segment!))
            return -(int)Errno.EIDRM; // Segment doesn't exist

        // Check permissions
        var requiredFlags = (flags & LinuxConstants.SHM_RDONLY) != 0
            ? LinuxConstants.SHM_R
            : LinuxConstants.SHM_R | LinuxConstants.SHM_W;
        if (!CheckPermission(segment, engine.Owner is FiberTask t ? t.Process.EUID : 0,
                engine.Owner is FiberTask t2 ? t2.Process.EGID : 0, requiredFlags))
            return -(int)Errno.EACCES;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine, ownerProcess);
        // Determine protection
        var prot = (flags & LinuxConstants.SHM_RDONLY) != 0
            ? Protection.Read
            : Protection.Read | Protection.Write;

        if ((flags & LinuxConstants.SHM_REMAP) != 0 && addr == 0)
            return -(int)Errno.EINVAL;

        // Determine address
        var attachAddr = addr;
        if (attachAddr == 0)
        {
            // Auto-allocate: find free region
            attachAddr = FindFreeRegion(vmaManager, segment.MappedSize);
            if (attachAddr == 0)
                return -(int)Errno.ENOMEM;
        }
        else
        {
            // Handle SHM_RND
            if ((flags & LinuxConstants.SHM_RND) != 0)
                attachAddr &= ~0xFFFu; // Round down to page boundary

            // Check alignment
            if ((attachAddr & 0xFFFu) != 0)
                return -(int)Errno.EINVAL;
        }

        var taskSize = vmaManager.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (!TryComputeRangeEnd(attachAddr, segment.MappedSize, taskSize, out var attachEnd))
            return -(int)Errno.EINVAL;

        // Check for overlapping VMAs unless SHM_REMAP is specified
        var hasExistingVmas = false;
        vmaManager.VisitVmAreasInRange(attachAddr, attachEnd, _ => hasExistingVmas = true);
        if (hasExistingVmas)
        {
            if ((flags & LinuxConstants.SHM_REMAP) != 0)
            {
                // SHM_REMAP: unmap existing mappings at this address
                ProcessAddressSpaceSync.Munmap(vmaManager, engine, attachAddr, segment.MappedSize, ownerProcess);
                DropAttachmentsInRange(vmaManager, attachAddr, attachEnd, pid);
            }
            else
            {
                // Without SHM_REMAP, overlapping mappings are an error
                return -(int)Errno.EINVAL;
            }
        }

        // Create VmArea for this mapping
        LinuxFile? segmentFile = null;
        AddressSpace? sharedObject = null;
        try
        {
            segmentFile = new LinuxFile(segment.BackingFile.LivePath, FileFlags.O_RDWR,
                LinuxFile.ReferenceKind.MmapHold, segment.BackingFile.OpenedInode);
            sharedObject = ((MappingBackedInode)segmentFile.OpenedInode!).AcquireMappingRef();

            var vma = new VmArea
            {
                Start = attachAddr,
                End = attachEnd,
                Perms = prot,
                Flags = MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous,
                FileMapping = new VmaFileMapping(segmentFile),
                Offset = 0,
                Name = $"[sysv shm:{shmid}]",
                VmMapping = sharedObject,
                VmPgoff = 0
            };
            segmentFile = null;
            sharedObject = null;

            // Add VmArea to manager
            vmaManager.AddVma(vma);
            // Record attachment
            var attach = new SysVShmAttach
            {
                Pid = pid,
                BaseAddr = attachAddr,
                Shmid = shmid,
                Prot = prot,
                AddressSpace = vmaManager
            };
            _attaches.Add(attach);

            segment.NAttch++;
            segment.ATime = DateTime.UtcNow;
            segment.Lpid = pid;

            return attachAddr;
        }
        catch
        {
            sharedObject?.Release();
            segmentFile?.Close();
            return -(int)Errno.ENOMEM;
        }
    }

    /// <summary>
    ///     Detach a shared memory segment from the process address space.
    /// </summary>
    /// <param name="addr">Address of the attachment</param>
    /// <param name="pid">Process ID</param>
    /// <param name="vmaManager">VmArea manager for the process</param>
    /// <param name="engine">Engine instance</param>
    /// <returns>0 on success, negative errno on failure</returns>
    public int ShmDt(uint addr, int pid, VMAManager vmaManager, Engine engine, Process? process = null)
    {
        var ownerProcess = process ?? (engine.Owner as FiberTask)?.Process;
        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine, ownerProcess);
        // Find the attachment by address and address-space ownership.
        // In CLONE_VM-like shared mm, peer processes can detach by address.
        var attachIndex = _attaches.FindIndex(a => a.BaseAddr == addr && ReferenceEquals(a.AddressSpace, vmaManager));
        if (attachIndex < 0)
            return -(int)Errno.EINVAL; // Not attached at this address for this process

        var attach = _attaches[attachIndex];
        _attaches.RemoveAt(attachIndex);

        // Find the segment
        if (!_segmentsByShmid.TryGetValue(attach.Shmid, out var segment))
            return -(int)Errno.EIDRM; // Segment was already removed

        // Unmap the VMA
        ProcessAddressSpaceSync.Munmap(vmaManager, engine, addr, segment.MappedSize, ownerProcess);

        // Update segment
        segment.NAttch--;
        segment.DTime = DateTime.UtcNow;
        segment.Lpid = pid;

        // Check if we should delete the segment
        if (segment.MarkedForDelete && segment.NAttch == 0)
            DestroySegment(segment);

        return 0;
    }

    /// <summary>
    ///     Control operations on a shared memory segment.
    /// </summary>
    /// <param name="shmid">Segment ID</param>
    /// <param name="cmd">Command (IPC_STAT, IPC_SET, IPC_RMID, etc.)</param>
    /// <param name="buf">User buffer for IPC_STAT/IPC_SET</param>
    /// <param name="engine">Engine instance for memory access</param>
    /// <param name="uid">Caller's UID</param>
    /// <param name="gid">Caller's GID</param>
    /// <param name="pid">Caller's PID</param>
    /// <returns>0 on success, negative errno on failure</returns>
    public int ShmCtl(int shmid, int cmd, uint buf, Engine engine, int uid, int gid, int pid)
    {
        if (!_segmentsByShmid.TryGetValue(shmid, out var segment))
            return -(int)Errno.EIDRM;

        bool ipc64 = (cmd & LinuxConstants.IPC_64) != 0;
        var actualCmd = cmd & ~LinuxConstants.IPC_64;

        switch (actualCmd)
        {
            case LinuxConstants.IPC_STAT:
                // Check read permission
                if (!CheckPermission(segment, uid, gid, LinuxConstants.SHM_R))
                    return -(int)Errno.EACCES;
                return WriteShmidDs(segment, buf, engine, ipc64);

            case LinuxConstants.IPC_SET:
                // Check write permission and ownership
                if (uid != 0 && uid != segment.Uid && uid != segment.CUid)
                    return -(int)Errno.EPERM;
                return ReadShmidDsForSet(segment, buf, engine, ipc64);

            case LinuxConstants.IPC_RMID:
                // Check ownership
                if (uid != 0 && uid != segment.Uid && uid != segment.CUid)
                    return -(int)Errno.EPERM;

                segment.MarkedForDelete = true;
                if (segment.Key != LinuxConstants.IPC_PRIVATE)
                {
                    _segmentsByKey.Remove(segment.Key);
                    segment.Key = LinuxConstants.IPC_PRIVATE;
                }

                if (segment.NAttch == 0)
                    DestroySegment(segment);

                return 0;

            case LinuxConstants.SHM_LOCK:
            case LinuxConstants.SHM_UNLOCK:
                // Requires CAP_IPC_LOCK, which we don't implement
                // For now, just return success (no-op)
                return 0;

            case LinuxConstants.SHM_STAT:
            case LinuxConstants.SHM_INFO:
            case LinuxConstants.SHM_STAT_ANY:
                // Not implemented in phase 1
                return -(int)Errno.ENOSYS;

            default:
                return -(int)Errno.EINVAL;
        }
    }

    /// <summary>
    ///     Called when a process exits to detach all its shared memory attachments.
    /// </summary>
    public void OnProcessExit(int pid, VMAManager vmaManager, Engine engine, Process? process = null)
    {
        var ownerProcess = process ?? (engine.Owner as FiberTask)?.Process;
        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine, ownerProcess);

        // Shared address space is still alive (e.g. CLONE_VM non-thread peer process exists).
        // Detach is tied to address-space teardown, not individual process exit in this case.
        if (vmaManager.GetSharedRefCount() > 1)
            return;

        for (var i = _attaches.Count - 1; i >= 0; i--)
        {
            var attach = _attaches[i];
            if (!ReferenceEquals(attach.AddressSpace, vmaManager)) continue;

            _attaches.RemoveAt(i);

            if (_segmentsByShmid.TryGetValue(attach.Shmid, out var segment))
            {
                // [P1] Must munmap the VmArea to avoid leaking page references
                ProcessAddressSpaceSync.Munmap(vmaManager, engine, attach.BaseAddr, segment.MappedSize, ownerProcess);

                segment.NAttch--;
                segment.DTime = DateTime.UtcNow;
                segment.Lpid = pid;

                if (segment.MarkedForDelete && segment.NAttch == 0)
                    DestroySegment(segment);
            }
        }
    }

    public long GetResidentBytesSnapshot()
    {
        long bytes = 0;
        foreach (var segment in _segmentsByShmid.Values)
            bytes += (long)(segment.BackingObject?.PageCount ?? 0) * LinuxConstants.PageSize;

        return bytes;
    }

    private void DestroySegment(SysVShmSegment segment)
    {
        _segmentsByShmid.Remove(segment.Shmid);
        if (segment.Key != LinuxConstants.IPC_PRIVATE)
            _segmentsByKey.Remove(segment.Key);
        segment.BackingFile.Close();
    }

    private bool CheckPermission(SysVShmSegment segment, int uid, int gid, int flags)
    {
        // Root has all permissions
        if (uid == 0) return true;

        int shift = segment.Uid == uid || segment.CUid == uid ? 6
            : segment.Gid == gid || segment.CGid == gid ? 3
            : 0;
        int requested = (flags | (flags << 3) | (flags << 6)) & 0x1c0;
        return (requested & ~(segment.Mode << (6 - shift))) == 0;
    }

    private uint FindFreeRegion(VMAManager vmaManager, uint size)
    {
        return vmaManager.FindFreeRegion(size);
    }

    private void DropAttachmentsInRange(VMAManager vmaManager, uint start, uint end, int lpid)
    {
        for (var i = _attaches.Count - 1; i >= 0; i--)
        {
            var attach = _attaches[i];
            if (!ReferenceEquals(attach.AddressSpace, vmaManager)) continue;
            if (!_segmentsByShmid.TryGetValue(attach.Shmid, out var segment)) continue;
            var taskSize = vmaManager.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
            if (!TryComputeRangeEnd(attach.BaseAddr, segment.MappedSize, taskSize, out var attachEnd)) continue;
            if (attach.BaseAddr >= end || attachEnd <= start) continue;

            _attaches.RemoveAt(i);

            segment.NAttch--;
            segment.DTime = DateTime.UtcNow;
            segment.Lpid = lpid;
            if (segment.MarkedForDelete && segment.NAttch == 0)
                DestroySegment(segment);
        }
    }

    private static bool TryComputeRangeEnd(uint start, uint size, uint taskSize, out uint end)
    {
        end = 0;
        if (size == 0) return false;
        var end64 = (ulong)start + size;
        if (end64 > taskSize) return false;
        end = (uint)end64;
        return end > start;
    }

    // i386 shmid_ds and shmid64_ds from Linux UAPI. The latter stores the 64-bit
    // timestamps in adjacent little-endian words and extends UID/GID and PID fields.
    private const int LegacyShmidDsSize = 48;
    private const int Ipc64ShmidDsSize = 84;

    private static int WriteShmidDs(SysVShmSegment segment, uint buf, Engine engine, bool ipc64)
    {
        var isAArch64 = engine.IsAArch64;
        Span<byte> data = stackalloc byte[isAArch64 ? 112 : ipc64 ? Ipc64ShmidDsSize : LegacyShmidDsSize];
        data.Clear();
        int mode = segment.Mode | (segment.MarkedForDelete ? 0x200 : 0); // SHM_DEST
        BinaryPrimitives.WriteInt32LittleEndian(data, segment.Key);
        if (isAArch64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data[4..], segment.Uid);
            BinaryPrimitives.WriteInt32LittleEndian(data[8..], segment.Gid);
            BinaryPrimitives.WriteInt32LittleEndian(data[12..], segment.CUid);
            BinaryPrimitives.WriteInt32LittleEndian(data[16..], segment.CGid);
            BinaryPrimitives.WriteInt32LittleEndian(data[20..], mode);
            BinaryPrimitives.WriteUInt64LittleEndian(data[48..], segment.Size);
            BinaryPrimitives.WriteInt64LittleEndian(data[56..], ToUnixSeconds(segment.ATime));
            BinaryPrimitives.WriteInt64LittleEndian(data[64..], ToUnixSeconds(segment.DTime));
            BinaryPrimitives.WriteInt64LittleEndian(data[72..], ToUnixSeconds(segment.CTime));
            BinaryPrimitives.WriteInt32LittleEndian(data[80..], segment.Cpid);
            BinaryPrimitives.WriteInt32LittleEndian(data[84..], segment.Lpid);
            BinaryPrimitives.WriteUInt64LittleEndian(data[88..], (ulong)segment.NAttch);
        }
        else if (ipc64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data[4..], segment.Uid);
            BinaryPrimitives.WriteInt32LittleEndian(data[8..], segment.Gid);
            BinaryPrimitives.WriteInt32LittleEndian(data[12..], segment.CUid);
            BinaryPrimitives.WriteInt32LittleEndian(data[16..], segment.CGid);
            BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)mode);
            BinaryPrimitives.WriteUInt32LittleEndian(data[36..], segment.Size);
            BinaryPrimitives.WriteInt64LittleEndian(data[40..], ToUnixSeconds(segment.ATime));
            BinaryPrimitives.WriteInt64LittleEndian(data[48..], ToUnixSeconds(segment.DTime));
            BinaryPrimitives.WriteInt64LittleEndian(data[56..], ToUnixSeconds(segment.CTime));
            BinaryPrimitives.WriteInt32LittleEndian(data[64..], segment.Cpid);
            BinaryPrimitives.WriteInt32LittleEndian(data[68..], segment.Lpid);
            BinaryPrimitives.WriteInt32LittleEndian(data[72..], segment.NAttch);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data[4..], ToLegacyId(segment.Uid));
            BinaryPrimitives.WriteUInt16LittleEndian(data[6..], ToLegacyId(segment.Gid));
            BinaryPrimitives.WriteUInt16LittleEndian(data[8..], ToLegacyId(segment.CUid));
            BinaryPrimitives.WriteUInt16LittleEndian(data[10..], ToLegacyId(segment.CGid));
            BinaryPrimitives.WriteUInt16LittleEndian(data[12..], (ushort)mode);
            BinaryPrimitives.WriteUInt32LittleEndian(data[16..], segment.Size);
            BinaryPrimitives.WriteInt32LittleEndian(data[20..], (int)ToUnixSeconds(segment.ATime));
            BinaryPrimitives.WriteInt32LittleEndian(data[24..], (int)ToUnixSeconds(segment.DTime));
            BinaryPrimitives.WriteInt32LittleEndian(data[28..], (int)ToUnixSeconds(segment.CTime));
            BinaryPrimitives.WriteUInt16LittleEndian(data[32..], (ushort)segment.Cpid);
            BinaryPrimitives.WriteUInt16LittleEndian(data[34..], (ushort)segment.Lpid);
            BinaryPrimitives.WriteUInt16LittleEndian(data[36..], (ushort)segment.NAttch);
        }

        return buf != 0 && engine.CopyToUser(buf, data) ? 0 : -(int)Errno.EFAULT;
    }

    private static int ReadShmidDsForSet(SysVShmSegment segment, uint buf, Engine engine, bool ipc64)
    {
        var isAArch64 = engine.IsAArch64;
        Span<byte> data = stackalloc byte[isAArch64 ? 112 : ipc64 ? Ipc64ShmidDsSize : LegacyShmidDsSize];
        if (buf == 0 || !engine.CopyFromUser(buf, data))
            return -(int)Errno.EFAULT;

        int uid = ipc64 ? BinaryPrimitives.ReadInt32LittleEndian(data[4..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int gid = ipc64 ? BinaryPrimitives.ReadInt32LittleEndian(data[8..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (uid == -1 || gid == -1)
            return -(int)Errno.EINVAL;

        segment.Uid = uid;
        segment.Gid = gid;
        segment.Mode = BinaryPrimitives.ReadUInt16LittleEndian(data[(ipc64 ? 20 : 12)..]) & 0x1ff;
        segment.CTime = DateTime.UtcNow;
        return 0;
    }

    private static long ToUnixSeconds(DateTime time) => new DateTimeOffset(time).ToUnixTimeSeconds();

    private static ushort ToLegacyId(int id) => (uint)id <= ushort.MaxValue ? (ushort)id : (ushort)65534;

}

/// <summary>
///     Extension methods for VMAManager to support SysV SHM.
/// </summary>
public static class VMAManagerExtensions
{
    /// <summary>
    ///     Add a VmArea directly (for SysV SHM use).
    /// </summary>
    public static void AddVma(this VMAManager vmaManager, VmArea vma)
    {
        vmaManager.AddVmaInternal(vma);
    }
}
