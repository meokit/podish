using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.Syscalls;

/// <summary>
/// Represents a System V shared memory segment.
/// </summary>
public class SysVShmSegment
{
    public int Key { get; set; } // IPC key (IPC_PRIVATE = 0)
    public int Shmid { get; set; } // Segment identifier
    public uint Size { get; set; } // Size in bytes (page-aligned)
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
    public MemoryObject BackingObject { get; set; } = null!;

    /// <summary>
    /// Number of pages in this segment.
    /// </summary>
    public uint PageCount => (Size + LinuxConstants.PageSize - 1) / LinuxConstants.PageSize;
}

/// <summary>
/// Represents an attachment of a process to a shared memory segment.
/// </summary>
public class SysVShmAttach
{
    public int Pid { get; set; } // Process that attached
    public uint BaseAddr { get; set; } // Virtual address where attached
    public int Shmid { get; set; } // Segment ID
    public Protection Prot { get; set; } // Mapping protection
}

/// <summary>
/// Manages System V shared memory segments globally.
/// This is a global IPC namespace shared across all processes in the emulator.
/// </summary>
public class SysVShmManager
{
    private readonly Dictionary<int, SysVShmSegment> _segmentsByShmid = new();
    private readonly Dictionary<int, SysVShmSegment> _segmentsByKey = new();
    private readonly List<SysVShmAttach> _attaches = new();
    private readonly object _lock = new();
    private int _nextShmid = 1;

    /// <summary>
    /// Get or create a shared memory segment.
    /// </summary>
    /// <param name="key">IPC key (0 = IPC_PRIVATE)</param>
    /// <param name="size">Size in bytes (will be rounded up to page boundary)</param>
    /// <param name="flags">IPC_CREAT, IPC_EXCL, and permission flags</param>
    /// <param name="uid">Creator/owner UID</param>
    /// <param name="gid">Creator/owner GID</param>
    /// <param name="pid">Creator PID</param>
    /// <returns>shmid on success, negative errno on failure</returns>
    public int ShmGet(int key, uint size, int flags, int uid, int gid, int pid)
    {
        // Round size up to page boundary
        var alignedSize = (size + LinuxConstants.PageSize - 1) & ~(uint)(LinuxConstants.PageSize - 1);
        if (alignedSize == 0) alignedSize = (uint)LinuxConstants.PageSize;

        lock (_lock)
        {
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

            // Create new segment
            var shmid = _nextShmid++;
            var segment = new SysVShmSegment
            {
                Key = key,
                Shmid = shmid,
                Size = alignedSize,
                Mode = flags & 0x1FF, // Lower 9 bits are permissions
                Uid = uid,
                Gid = gid,
                CUid = uid,
                CGid = gid,
                Cpid = pid,
                Lpid = pid,
                CTime = DateTime.UtcNow,
                ATime = DateTime.UtcNow,
                DTime = DateTime.UtcNow,
                NAttch = 0,
                MarkedForDelete = false,
                BackingObject = MemoryObjectManager.Instance.CreateAnonymous(true) // shared
            };

            _segmentsByShmid[shmid] = segment;
            if (key != LinuxConstants.IPC_PRIVATE)
                _segmentsByKey[key] = segment;

            return shmid;
        }
    }

    /// <summary>
    /// Attach a shared memory segment to the process address space.
    /// </summary>
    /// <param name="shmid">Segment ID</param>
    /// <param name="addr">Desired address (0 = auto-allocate)</param>
    /// <param name="flags">SHM_RDONLY, SHM_RND, SHM_REMAP</param>
    /// <param name="pid">Process ID</param>
    /// <param name="vmaManager">VMA manager for the process</param>
    /// <param name="engine">Engine instance</param>
    /// <returns>Attached address on success, negative errno on failure</returns>
    public long ShmAt(int shmid, uint addr, int flags, int pid, VMAManager vmaManager, Engine engine)
    {
        SysVShmSegment segment;
        lock (_lock)
        {
            if (!_segmentsByShmid.TryGetValue(shmid, out segment!))
                return -(int)Errno.EIDRM; // Segment doesn't exist

            // Check permissions
            var requiredFlags = (flags & LinuxConstants.SHM_RDONLY) != 0 ? LinuxConstants.SHM_R : LinuxConstants.SHM_R | LinuxConstants.SHM_W;
            if (!CheckPermission(segment, engine.Owner is FiberTask t ? t.Process.EUID : 0, 
                engine.Owner is FiberTask t2 ? t2.Process.EGID : 0, requiredFlags))
                return -(int)Errno.EACCES;
        }

        // Determine protection
        var prot = (flags & LinuxConstants.SHM_RDONLY) != 0
            ? Protection.Read
            : Protection.Read | Protection.Write;

        // Determine address
        var attachAddr = addr;
        if (attachAddr == 0)
        {
            // Auto-allocate: find free region
            attachAddr = FindFreeRegion(vmaManager, segment.Size);
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

            // Check for overlapping VMAs unless SHM_REMAP is specified
            var existingVmas = vmaManager.FindVMAsInRange(attachAddr, attachAddr + segment.Size);
            if (existingVmas.Count > 0)
            {
                if ((flags & LinuxConstants.SHM_REMAP) != 0)
                {
                    // SHM_REMAP: unmap existing mappings at this address
                    vmaManager.Munmap(attachAddr, segment.Size, engine);
                }
                else
                {
                    // Without SHM_REMAP, overlapping mappings are an error
                    return -(int)Errno.EINVAL;
                }
            }
        }

        // Create VMA for this mapping
        try
        {
            // Create a special VMA that references the segment's backing object
            var vma = new VMA
            {
                Start = attachAddr,
                End = attachAddr + segment.Size,
                Perms = prot,
                Flags = MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous,
                File = null,
                Offset = 0,
                FileSz = segment.Size,
                Name = $"[sysv shm:{shmid}]",
                MemoryObject = segment.BackingObject,
                ViewPageOffset = 0
            };
            segment.BackingObject.AddRef();

            // Add VMA to manager
            vmaManager.AddVma(vma);

            // Record attachment
            lock (_lock)
            {
                var attach = new SysVShmAttach
                {
                    Pid = pid,
                    BaseAddr = attachAddr,
                    Shmid = shmid,
                    Prot = prot
                };
                _attaches.Add(attach);

                segment.NAttch++;
                segment.ATime = DateTime.UtcNow;
                segment.Lpid = pid;
            }

            return attachAddr;
        }
        catch
        {
            return -(int)Errno.ENOMEM;
        }
    }

    /// <summary>
    /// Detach a shared memory segment from the process address space.
    /// </summary>
    /// <param name="addr">Address of the attachment</param>
    /// <param name="pid">Process ID</param>
    /// <param name="vmaManager">VMA manager for the process</param>
    /// <param name="engine">Engine instance</param>
    /// <returns>0 on success, negative errno on failure</returns>
    public int ShmDt(uint addr, int pid, VMAManager vmaManager, Engine engine)
    {
        lock (_lock)
        {
            // Find the attachment by address and pid (TGID)
            // Linux shmdt operates on the calling process's address space
            var attachIndex = _attaches.FindIndex(a => a.BaseAddr == addr && a.Pid == pid);
            if (attachIndex < 0)
                return -(int)Errno.EINVAL; // Not attached at this address for this process

            var attach = _attaches[attachIndex];
            _attaches.RemoveAt(attachIndex);

            // Find the segment
            if (!_segmentsByShmid.TryGetValue(attach.Shmid, out var segment))
                return -(int)Errno.EIDRM; // Segment was already removed

            // Unmap the VMA
            vmaManager.Munmap(addr, segment.Size, engine);

            // Update segment
            segment.NAttch--;
            segment.DTime = DateTime.UtcNow;
            segment.Lpid = pid;

            // Check if we should delete the segment
            if (segment.MarkedForDelete && segment.NAttch == 0)
                DestroySegment(segment);

            return 0;
        }
    }

    /// <summary>
    /// Control operations on a shared memory segment.
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
        lock (_lock)
        {
            if (!_segmentsByShmid.TryGetValue(shmid, out var segment))
                return -(int)Errno.EIDRM;

            // Handle IPC_64 flag - strip it from cmd
            var actualCmd = cmd & ~LinuxConstants.IPC_64;

            switch (actualCmd)
            {
                case LinuxConstants.IPC_STAT:
                    // Check read permission
                    if (!CheckPermission(segment, uid, gid, LinuxConstants.SHM_R))
                        return -(int)Errno.EACCES;
                    return WriteShmidDs(segment, buf, engine);

                case LinuxConstants.IPC_SET:
                    // Check write permission and ownership
                    if (segment.Uid != uid && uid != 0)
                        return -(int)Errno.EPERM;
                    return ReadShmidDsForSet(segment, buf, engine);

                case LinuxConstants.IPC_RMID:
                    // Check ownership
                    if (segment.CUid != uid && uid != 0)
                        return -(int)Errno.EPERM;
                    
                    segment.MarkedForDelete = true;
                    segment.Lpid = pid;
                    segment.CTime = DateTime.UtcNow;

                    // If no attachments, destroy immediately
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
    }

    /// <summary>
    /// Called when a process exits to detach all its shared memory attachments.
    /// </summary>
    public void OnProcessExit(int pid, VMAManager vmaManager, Engine engine)
    {
        lock (_lock)
        {
            for (var i = _attaches.Count - 1; i >= 0; i--)
            {
                var attach = _attaches[i];
                if (attach.Pid != pid) continue;

                _attaches.RemoveAt(i);

                if (_segmentsByShmid.TryGetValue(attach.Shmid, out var segment))
                {
                    // [P1] Must munmap the VMA to avoid leaking page references
                    vmaManager.Munmap(attach.BaseAddr, segment.Size, engine);

                    segment.NAttch--;
                    segment.DTime = DateTime.UtcNow;
                    segment.Lpid = pid;

                    if (segment.MarkedForDelete && segment.NAttch == 0)
                        DestroySegment(segment);
                }
            }
        }
    }

    private void DestroySegment(SysVShmSegment segment)
    {
        _segmentsByShmid.Remove(segment.Shmid);
        if (segment.Key != LinuxConstants.IPC_PRIVATE)
            _segmentsByKey.Remove(segment.Key);
        segment.BackingObject.Release();
    }

    private bool CheckPermission(SysVShmSegment segment, int uid, int gid, int flags)
    {
        // Root has all permissions
        if (uid == 0) return true;

        var mode = segment.Mode;
        if (segment.Uid == uid)
        {
            // Owner permissions
            if ((flags & LinuxConstants.SHM_R) != 0 && (mode & 0400) == 0) return false;
            if ((flags & LinuxConstants.SHM_W) != 0 && (mode & 0200) == 0) return false;
        }
        else if (segment.Gid == gid)
        {
            // Group permissions
            if ((flags & LinuxConstants.SHM_R) != 0 && (mode & 0040) == 0) return false;
            if ((flags & LinuxConstants.SHM_W) != 0 && (mode & 0020) == 0) return false;
        }
        else
        {
            // Other permissions
            if ((flags & LinuxConstants.SHM_R) != 0 && (mode & 0004) == 0) return false;
            if ((flags & LinuxConstants.SHM_W) != 0 && (mode & 0002) == 0) return false;
        }

        return true;
    }

    private uint FindFreeRegion(VMAManager vmaManager, uint size)
    {
        // Start from a reasonable address
        var addr = 0x10000000u; // 256 MB
        var end = addr + size;

        while (end < LinuxConstants.TaskSize32)
        {
            var vmas = vmaManager.FindVMAsInRange(addr, end);
            if (vmas.Count == 0)
                return addr;

            // Move past the overlapping VMA
            var maxEnd = addr;
            foreach (var vma in vmas)
                if (vma.End > maxEnd)
                    maxEnd = vma.End;

            addr = (maxEnd + LinuxConstants.PageSize - 1) & ~(uint)(LinuxConstants.PageSize - 1);
            end = addr + size;
        }

        return 0; // No free region found
    }

    private int WriteShmidDs(SysVShmSegment segment, uint buf, Engine engine)
    {
        // Write shmid_ds structure to user memory
        // Structure layout (i386 obsolete/compat):
        // - ipc_perm (16 bytes)
        // - shm_segsz (4 bytes)
        // - shm_atime (4 bytes)
        // - shm_dtime (4 bytes)
        // - shm_ctime (4 bytes)
        // - shm_cpid (2 bytes)
        // - shm_lpid (2 bytes)
        // - shm_nattch (2 bytes)
        // - shm_unused (2 bytes)
        // - shm_unused2 (4 bytes)
        // - shm_unused3 (4 bytes)
        // Total: 48 bytes

        if (buf == 0) return -(int)Errno.EINVAL;

        var data = new byte[48];
        var offset = 0;

        // ipc_perm structure (16 bytes)
        BitConverter.TryWriteBytes(data.AsSpan(offset), segment.Key); offset += 4;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Uid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Gid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.CUid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.CGid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Mode); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Shmid); offset += 2; // seq (reuse shmid)

        // Rest of shmid_ds
        BitConverter.TryWriteBytes(data.AsSpan(offset), (int)segment.Size); offset += 4;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (int)segment.ATime.Subtract(DateTime.UnixEpoch).TotalSeconds); offset += 4;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (int)segment.DTime.Subtract(DateTime.UnixEpoch).TotalSeconds); offset += 4;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (int)segment.CTime.Subtract(DateTime.UnixEpoch).TotalSeconds); offset += 4;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Cpid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.Lpid); offset += 2;
        BitConverter.TryWriteBytes(data.AsSpan(offset), (ushort)segment.NAttch); offset += 2;
        // shm_unused, shm_unused2, shm_unused3 are zero

        if (!engine.CopyToUser(buf, data))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private int ReadShmidDsForSet(SysVShmSegment segment, uint buf, Engine engine)
    {
        // Read ipc_perm portion from user memory and update segment
        if (buf == 0) return -(int)Errno.EINVAL;

        var data = new byte[16];
        if (!engine.CopyFromUser(buf, data))
            return -(int)Errno.EFAULT;

        // Only uid, gid, and mode can be changed
        var uid = BitConverter.ToUInt16(data, 2);
        var gid = BitConverter.ToUInt16(data, 4);
        var mode = BitConverter.ToUInt16(data, 10);

        segment.Uid = uid;
        segment.Gid = gid;
        segment.Mode = mode;
        segment.CTime = DateTime.UtcNow;

        return 0;
    }
}

/// <summary>
/// Extension methods for VMAManager to support SysV SHM.
/// </summary>
public static class VMAManagerExtensions
{
    /// <summary>
    /// Add a VMA directly (for SysV SHM use).
    /// </summary>
    public static void AddVma(this VMAManager vmaManager, VMA vma)
    {
        vmaManager.AddVmaInternal(vma);
    }
}
