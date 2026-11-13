using System.Buffers;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysBrk(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var newBrk = a1;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (newBrk == 0) return (int)BrkAddr;

        if (newBrk < BrkBase)
            return (int)BrkAddr;
        if (newBrk > taskSize)
            return (int)BrkAddr;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        if (newBrk > BrkAddr)
        {
            var start = (BrkAddr + 0xFFF) & ~0xFFFu;
            var end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
                try
                {
                    var brkFlags = MapFlags.Private | MapFlags.Anonymous | MapFlags.FixedNoReplace;
                    var lockOnFault = Mem.LockFutureMappings && Mem.LockFutureOnFault;
                    if (Mem.LockFutureMappings)
                    {
                        if (engine.Owner is FiberTask brkTask && brkTask.Process != null &&
                            brkTask.Process.EUID != 0 &&
                            brkTask.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var brkLimit))
                        {
                            if (brkLimit.Soft == 0 || (ulong)Mem.GetTotalLockedVmBytes() + (end - start) > brkLimit.Soft)
                                return (int)BrkAddr;
                        }
                        brkFlags |= MapFlags.Locked;
                    }

                    var extended = Mem.TryExtendHeapVma(start, end, brkFlags, lockOnFault);
                    if (!extended)
                    {
                        var mapped = ProcessAddressSpaceSync.Mmap(Mem, engine, start, end - start,
                            Protection.Read | Protection.Write, brkFlags, null, 0,
                            "HEAP");
                        if (mapped != start)
                            return (int)BrkAddr;
                    }

                    if ((brkFlags & MapFlags.Locked) != 0)
                    {
                        if (lockOnFault)
                        {
                            if (!extended && Mem.FindVmArea(start) is { } lockedHeapVma)
                                lockedHeapVma.LockOnFault = true;
                        }
                        else if (Mem.LockRange(start, end - start, false, engine) < 0 &&
                                 Mem.FindVmArea(start) is { } heapVma)
                        {
                            heapVma.Flags &= ~MapFlags.Locked;
                        }
                    }
                }
                catch
                {
                    return (int)BrkAddr;
                }

            BrkAddr = newBrk;
            return (int)BrkAddr;
        }

        if (newBrk < BrkAddr)
        {
            var start = (newBrk + 0xFFF) & ~0xFFFu;
            var end = (BrkAddr + 0xFFF) & ~0xFFFu;
            if (end > start) ProcessAddressSpaceSync.Munmap(Mem, engine, start, end - start);

            BrkAddr = newBrk;
        }

        return (int)BrkAddr;
    }

    private async ValueTask<int> SysMmap2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        var prot = (int)a3;
        var flags = (int)a4;
        var fd = (int)a5;
        var offset = (long)a6 * 4096;

        var isAnon = (flags & (int)MapFlags.Anonymous) != 0;
        LinuxFile? f = null;
        LinuxFile? mmapFile = null;
        // Linux resolves a file descriptor before do_mmap validates length.
        if (!isAnon)
        {
            f = GetFD(fd);
            const FileFlags O_PATH = (FileFlags)0x200000;
            if (f == null || (f.Flags & O_PATH) != 0) return -(int)Errno.EBADF;
        }
        if (len == 0) return -(int)Errno.EINVAL;
        if (len > uint.MaxValue - LinuxConstants.PageOffsetMask) return -(int)Errno.ENOMEM;

        var isNoReplace = (flags & (int)MapFlags.FixedNoReplace) != 0;
        var isFixed = (flags & (int)MapFlags.Fixed) != 0 || isNoReplace;
        if (isFixed && (addr & LinuxConstants.PageOffsetMask) != 0)
            return -(int)Errno.EINVAL;
        if (!isFixed) addr &= LinuxConstants.PageMask;
        if (isFixed && addr == 0)
            return -(int)Errno.EINVAL;

        var mapType = flags & 0xF;
        var sharedValidate = mapType == 3;
        if (mapType != 1 && mapType != 2 && (!sharedValidate || isAnon))
            return -(int)Errno.EINVAL;
        var isShared = mapType == 1 || sharedValidate;
        if (sharedValidate)
        {
            // Linux's legacy mmap flags; unknown bits are ignored for MAP_SHARED
            // but explicitly rejected by MAP_SHARED_VALIDATE (MAP_SYNC is not supported).
            const int legacyFlags = 0x01 | 0x02 | 0x10 | 0x20 | 0x40 | 0x100 | 0x800 | 0x1000 |
                                    0x2000 | 0x4000 | 0x8000 | 0x10000 | 0x20000 | 0x40000 |
                                    0x100000;
            if ((flags & ~legacyFlags) != 0) return -(int)Errno.EOPNOTSUPP;
            flags = (flags & ~0xF) | (int)MapFlags.Shared;
        }

        var mapLen = (len + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        if (isFixed && (ulong)addr + mapLen > (Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32))
            return -(int)Errno.ENOMEM;
        if (isNoReplace)
        {
            var hasOverlap = false;
            Mem.VisitVmAreasInRange(addr, addr + mapLen, _ => hasOverlap = true);
            if (hasOverlap)
                return -(int)Errno.EEXIST;
        }

        if (isAnon)
        {
            fd = -1;
            offset = 0;
        }
        else
        {
            var inode = f!.OpenedInode;
            if (inode == null) return -(int)Errno.EBADF;
            if ((f.Flags & (FileFlags.O_WRONLY | FileFlags.O_RDWR)) == FileFlags.O_WRONLY)
                return -(int)Errno.EACCES;
            if (!inode.SupportsMmap) return -(int)Errno.ENODEV;
            if (isShared && (prot & (int)Protection.Write) != 0)
            {
                if (inode is TmpfsInode { HasWriteSeal: true }) return -(int)Errno.EPERM;
                var canWrite = (f.Flags & (FileFlags.O_WRONLY | FileFlags.O_RDWR)) != 0;
                if (!canWrite) return -(int)Errno.EACCES;
            }
        }

        var futureLock = Mem.LockFutureMappings && (flags & (int)MapFlags.Locked) == 0;
        if (futureLock)
            flags |= (int)MapFlags.Locked;

        if ((flags & (int)MapFlags.Locked) != 0 &&
            engine.Owner is FiberTask lockTask && lockTask.Process != null &&
            lockTask.Process.EUID != 0 &&
            lockTask.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var memlockLimit))
        {
            if (memlockLimit.Soft == 0 || (ulong)Mem.GetTotalLockedVmBytes() + mapLen > memlockLimit.Soft)
                return -(int)Errno.EAGAIN;
        }

        try
        {
            if (f != null)
                mmapFile = new LinuxFile(f.LivePath, f.Flags, LinuxFile.ReferenceKind.MmapHold, f.OpenedInode);

            var res = ProcessAddressSpaceSync.Mmap(Mem, engine, addr, len, (Protection)prot, (MapFlags)flags,
                mmapFile, offset, "MMAP2");
            mmapFile = null; // ownership transferred to VMAs

            const int MAP_POPULATE = 0x8000;
            var endAddr = (uint)res + (uint)mapLen;
            if ((flags & (int)MapFlags.Locked) != 0)
            {
                if (futureLock && Mem.LockFutureOnFault)
                {
                    if (Mem.FindVmArea((uint)res) is { } lockedVma)
                        lockedVma.LockOnFault = true;
                }
                else
                {
                    if (Mem.LockRange((uint)res, (uint)mapLen, false, engine) < 0 &&
                        Mem.FindVmArea((uint)res) is { } mappedVma)
                    {
                        mappedVma.Flags &= ~MapFlags.Locked;
                        mappedVma.LockOnFault = false;
                    }
                }
            }
            else if ((flags & MAP_POPULATE) != 0)
            {
                for (var p = (uint)res; p < endAddr; p += LinuxConstants.PageSize)
                    Mem.HandleFault(p, false, engine);
            }

            return (int)res;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SysMmap2] Unexpected exception during mmap: {Message}", ex.Message);
            return -(int)Errno.ENOMEM;
        }
        finally
        {
            mmapFile?.Close();
        }
    }

    private async ValueTask<int> SysMunmap(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (a2 == 0) return -(int)Errno.EINVAL;
        if ((a1 & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        var roundedLength = ((ulong)a2 + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        if (a1 >= taskSize || (ulong)a1 + roundedLength > taskSize) return -(int)Errno.EINVAL;
        var rc = ProcessAddressSpaceSync.Munmap(Mem, engine, a1, a2);
        return rc < 0 ? rc : 0;
    }

    private async ValueTask<int> SysMprotect(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        var prot = (Protection)a3;

        if (len == 0) return 0;
        if ((addr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        if ((a3 & ~7u) != 0) return -(int)Errno.EINVAL;
        var roundedLength = ((ulong)len + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        var endLong = (ulong)addr + roundedLength;
        if (endLong > (Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32))
            return -(int)Errno.ENOMEM;

        if (Mem.TryGetVmaWindowForSyscall(addr, (uint)endLong, out var startIndex, out var endIndexExclusive))
        {
            var vmas = Mem.VMAs;
            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = vmas[i];
                if ((prot & Protection.Write) != 0 && (vma.Flags & MapFlags.Shared) != 0 && vma.File != null &&
                    ((vma.File.Flags & FileFlags.O_RDWR) == 0 || vma.LogicalInode is TmpfsInode { HasWriteSeal: true }))
                    return -(int)Errno.EACCES;
            }
        }

        return ProcessAddressSpaceSync.Mprotect(Mem, engine, addr, (uint)roundedLength, prot);
    }

    private async ValueTask<int> SysMadvise(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const uint MADV_NORMAL = 0;
        const uint MADV_RANDOM = 1;
        const uint MADV_SEQUENTIAL = 2;
        const uint MADV_WILLNEED = 3;
        const uint MADV_DONTNEED = 4;
        const uint MADV_FREE = 8;
        const uint MADV_REMOVE = 9;
        const uint MADV_DONTFORK = 10;
        const uint MADV_DOFORK = 11;
        const uint MADV_MERGEABLE = 12;
        const uint MADV_UNMERGEABLE = 13;
        const uint MADV_HUGEPAGE = 14;
        const uint MADV_NOHUGEPAGE = 15;
        const uint MADV_DONTDUMP = 16;
        const uint MADV_DODUMP = 17;
        const uint MADV_WIPEONFORK = 18;
        const uint MADV_KEEPONFORK = 19;
        const uint MADV_COLD = 20;
        const uint MADV_PAGEOUT = 21;

        // There is no hardware memory-failure injection facility in this runtime.
        if (a3 is 100 or 101) return -(int)Errno.EINVAL; // MADV_HWPOISON / MADV_SOFT_OFFLINE
        if (a2 == 0) return 0;
        if ((a1 & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        var alignedLen = a2 > uint.MaxValue - LinuxConstants.PageOffsetMask
            ? uint.MaxValue & LinuxConstants.PageMask
            : (a2 + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        var end = (ulong)a1 + alignedLen;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (end > taskSize) return -(int)Errno.ENOMEM;

        var knownAdvice = a3 is MADV_NORMAL or MADV_RANDOM or MADV_SEQUENTIAL or MADV_WILLNEED or
                          MADV_DONTNEED or MADV_FREE or MADV_REMOVE or MADV_DONTFORK or MADV_DOFORK or
                          MADV_MERGEABLE or MADV_UNMERGEABLE or MADV_HUGEPAGE or MADV_NOHUGEPAGE or
                          MADV_DONTDUMP or MADV_DODUMP or MADV_WIPEONFORK or MADV_KEEPONFORK or
                          MADV_COLD or MADV_PAGEOUT;
        if (!knownAdvice) return -(int)Errno.EINVAL;

        if (!Mem.TryGetContiguousVmaWindow(a1, (uint)end, out var startIndex, out var endIndexExclusive))
            return -(int)Errno.ENOMEM;

        var vmas = Mem.VMAs;
        if (a3 == MADV_REMOVE)
        {
            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = vmas[i];
                if ((vma.Perms & Protection.Write) == 0 || (vma.Flags & MapFlags.Shared) == 0 || vma.LogicalInode is not TmpfsInode)
                    return -(int)Errno.EINVAL;
                if (vma.LogicalInode is TmpfsInode { HasWriteSeal: true })
                    return -(int)Errno.EPERM;
            }
            return MadviseRemoveRange(engine, a1, (uint)end, startIndex, endIndexExclusive);
        }

        if (a3 is MADV_MERGEABLE or MADV_UNMERGEABLE)
        {
            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = vmas[i];
                if ((vma.Flags & MapFlags.Shared) != 0 && (vma.Perms & Protection.Write) == 0)
                    return -(int)Errno.EINVAL;
            }
        }

        if (a3 == MADV_FREE)
        {
            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = vmas[i];
                if (vma.IsFileBacked || (vma.Flags & MapFlags.Private) == 0 || (vma.Flags & MapFlags.Shared) != 0)
                    return -(int)Errno.EINVAL;
            }
        }

        return a3 switch
        {
            MADV_DONTNEED => ProcessAddressSpaceSync.MadviseDontNeed(Mem, engine, a1, alignedLen),
            MADV_DONTFORK => ProcessAddressSpaceSync.MadviseForkInheritance(Mem, engine, a1, alignedLen, true),
            MADV_DOFORK => ProcessAddressSpaceSync.MadviseForkInheritance(Mem, engine, a1, alignedLen, false),
            MADV_WIPEONFORK => ProcessAddressSpaceSync.MadviseWipeOnFork(Mem, engine, a1, alignedLen, true),
            MADV_KEEPONFORK => ProcessAddressSpaceSync.MadviseWipeOnFork(Mem, engine, a1, alignedLen, false),
            _ => 0
        };
    }

    private int MadviseRemoveRange(Engine engine, uint start, uint end, int startIndex, int endIndexExclusive)
    {
        var vmas = Mem.VMAs;
        var mutationCtx = CreateFileMutationContext(engine);
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            var vma = vmas[i];
            var overlapStart = Math.Max(vma.Start, start);
            var overlapEnd = Math.Min(vma.End, end);
            if (overlapStart >= overlapEnd)
                continue;

            var inode = vma.BackingInode ?? vma.LogicalInode;
            if (inode == null)
                return -(int)Errno.EINVAL;

            var fileOffset = vma.Offset + (long)(overlapStart - vma.Start);
            var holeLen = (long)(overlapEnd - overlapStart);
            var rc = inode.PunchHole(fileOffset, holeLen, mutationCtx);
            if (rc < 0)
                return rc;
        }
        return 0;
    }

    private async ValueTask<int> SysMsync(Engine engine, uint addr, uint len, uint flags, uint a4, uint a5,
        uint a6)
    {
        const uint MS_ASYNC = 1, MS_INVALIDATE = 2, MS_SYNC = 4;
        if ((addr & LinuxConstants.PageOffsetMask) != 0 || (flags & ~7u) != 0 ||
            (flags & (MS_ASYNC | MS_SYNC)) == (MS_ASYNC | MS_SYNC))
            return -(int)Errno.EINVAL;
        if (len == 0) return 0;
        var roundedLength = ((ulong)len + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        var end = (ulong)addr + roundedLength;
        if (end > (Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32)) return -(int)Errno.ENOMEM;

        var hasHole = !Mem.TryGetContiguousVmaWindow(addr, (uint)end, out var startIndex, out var endIndexExclusive);
        if ((flags & MS_INVALIDATE) != 0 &&
            Mem.TryGetVmaWindowForSyscall(addr, (uint)end, out var winStart, out var winEnd))
        {
            var vmas = Mem.VMAs;
            for (var i = winStart; i < winEnd; i++)
            {
                if ((vmas[i].Flags & MapFlags.Locked) != 0)
                    return -(int)Errno.EBUSY;
            }
        }
        _ = startIndex;
        _ = endIndexExclusive;
        ProcessAddressSpaceSync.SyncSharedRange(Mem, engine, addr, len);
        return hasHole ? -(int)Errno.ENOMEM : 0;
    }

    private async ValueTask<int> SysMincore(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        var vecPtr = a3;

        if ((addr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        if (len == 0) return 0;
        if (vecPtr == 0) return -(int)Errno.EFAULT;

        var roundedLen = ((ulong)len + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        var end = (ulong)addr + roundedLen;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (end > taskSize) return -(int)Errno.ENOMEM;

        if (!Mem.TryGetContiguousVmaWindow(addr, (uint)end, out var startIndex, out var endIndexExclusive))
            return -(int)Errno.ENOMEM;

        var vmas = Mem.VMAs;
        var pageCount = (int)(roundedLen / LinuxConstants.PageSize);
        const int ChunkSize = 256;
        Span<byte> chunk = stackalloc byte[ChunkSize];

        var vmaIdx = startIndex;
        var currentVma = vmas[vmaIdx];
        var written = 0;

        while (written < pageCount)
        {
            var batch = Math.Min(ChunkSize, pageCount - written);
            for (var i = 0; i < batch; i++)
            {
                var p = addr + (uint)(written + i) * LinuxConstants.PageSize;
                while (p >= currentVma.End && vmaIdx + 1 < endIndexExclusive)
                    currentVma = vmas[++vmaIdx];

                var isResident = false;
                if (p >= currentVma.Start && p < currentVma.End)
                {
                    var pageIndex = currentVma.GetPageIndex(p);
                    if (currentVma.IsFileBacked)
                    {
                        if (Mem.PageMapping.TryGet(p, out _))
                            isResident = true;
                        else if (currentVma.VmAnonVma is { } anon && anon.PeekPage(pageIndex) != IntPtr.Zero)
                            isResident = true;
                        else if (currentVma.VmMapping is { } mapping && mapping.PeekPage(pageIndex) != IntPtr.Zero)
                            isResident = true;
                        else if (currentVma.LogicalInode is IHostMappedCacheDropper dropper &&
                                 dropper.CheckPageResidency((long)pageIndex) == HostPageResidencyStatus.Resident)
                            isResident = true;
                    }
                    else
                    {
                        if (currentVma.VmAnonVma is { } anon && anon.PeekPage(pageIndex) != IntPtr.Zero)
                            isResident = true;
                        else if ((currentVma.Flags & MapFlags.Locked) != 0 && Mem.PageMapping.TryGet(p, out _))
                            isResident = true;
                    }
                }

                chunk[i] = (byte)(isResident ? 1 : 0);
            }

            if (!engine.CopyToUser(vecPtr + (uint)written, chunk[..batch]))
                return -(int)Errno.EFAULT;

            written += batch;
        }

        return 0;
    }

    private async ValueTask<int> SysMlock(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysMlock2(engine, a1, a2, 0, a4, a5, a6);
    }

    private async ValueTask<int> SysMlock2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        var flags = a3;

        const uint MLOCK_ONFAULT = 0x01;
        if ((flags & ~MLOCK_ONFAULT) != 0) return -(int)Errno.EINVAL;
        if (len == 0) return 0;

        var start = addr & LinuxConstants.PageMask;
        var end = ((ulong)addr + len + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (end <= start || end > taskSize) return -(int)Errno.ENOMEM;

        if (!Mem.TryGetContiguousVmaWindow(start, (uint)end, out _, out _))
            return -(int)Errno.ENOMEM;

        if (engine.Owner is FiberTask task && task.Process != null &&
            task.Process.EUID != 0 && task.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var limit))
        {
            if (limit.Soft == 0) return -(int)Errno.EPERM;
            var totalLockedAfter = Mem.GetTotalLockedVmBytes() + Mem.GetAdditionalLockBytes(start, (uint)end);
            if ((ulong)totalLockedAfter > limit.Soft) return -(int)Errno.ENOMEM;
        }

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        return Mem.LockRange(start, (uint)(end - start), (flags & MLOCK_ONFAULT) != 0, engine);
    }

    private async ValueTask<int> SysMunlock(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        if (len == 0) return 0;

        var start = addr & LinuxConstants.PageMask;
        var end = ((ulong)addr + len + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (end <= start || end > taskSize) return -(int)Errno.ENOMEM;

        if (!Mem.TryGetContiguousVmaWindow(start, (uint)end, out _, out _))
            return -(int)Errno.ENOMEM;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        return Mem.UnlockRange(start, (uint)(end - start));
    }

    private async ValueTask<int> SysMlockall(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var flags = a1;
        const uint MCL_CURRENT = 1, MCL_FUTURE = 2, MCL_ONFAULT = 4;
        if (flags == 0 || (flags & ~(MCL_CURRENT | MCL_FUTURE | MCL_ONFAULT)) != 0)
            return -(int)Errno.EINVAL;
        if ((flags & MCL_ONFAULT) != 0 && (flags & (MCL_CURRENT | MCL_FUTURE)) == 0)
            return -(int)Errno.EINVAL;

        if (engine.Owner is FiberTask task && task.Process != null && task.Process.EUID != 0 &&
            task.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var limit))
        {
            if (limit.Soft == 0) return -(int)Errno.EPERM;
            if ((flags & MCL_CURRENT) != 0 && (ulong)Mem.GetTotalVmBytes() > limit.Soft)
                return -(int)Errno.ENOMEM;
        }

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        return Mem.MlockAll((flags & MCL_CURRENT) != 0, (flags & MCL_FUTURE) != 0, (flags & MCL_ONFAULT) != 0, engine);
    }

    private async ValueTask<int> SysMunlockall(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        Mem.MunlockAll();
        return 0;
    }

    private async ValueTask<int> SysMremap(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // void *mremap(void *old_address, size_t old_size, size_t new_size, int flags, ... void *new_address);

        var oldAddr = a1;
        var oldLen = a2;
        var newLen = a3;
        var flags = (int)a4;
        var newAddr = a5;

        const int MREMAP_MAYMOVE = 1;
        const int MREMAP_FIXED = 2;

        if (newLen == 0) return -(int)Errno.EINVAL;
        if ((oldAddr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        if ((flags & ~(MREMAP_MAYMOVE | MREMAP_FIXED)) != 0) return -(int)Errno.EINVAL;
        if (oldLen > uint.MaxValue - LinuxConstants.PageOffsetMask ||
            newLen > uint.MaxValue - LinuxConstants.PageOffsetMask) return -(int)Errno.ENOMEM;

        var mayMove = (flags & MREMAP_MAYMOVE) != 0;
        var isFixed = (flags & MREMAP_FIXED) != 0;

        if (isFixed && !mayMove) return -(int)Errno.EINVAL;
        if (isFixed && (newAddr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        // Align sizes to page boundaries
        var oldLenAligned = (oldLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        var newLenAligned = (newLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if ((ulong)oldAddr + oldLenAligned > taskSize) return -(int)Errno.EFAULT;
        if (isFixed)
        {
            if ((ulong)newAddr + newLenAligned > taskSize) return -(int)Errno.EINVAL;
            if ((ulong)newAddr < (ulong)oldAddr + oldLenAligned &&
                (ulong)oldAddr < (ulong)newAddr + newLenAligned) return -(int)Errno.EINVAL;
            if (newAddr < LinuxConstants.MinMmapAddr) return -(int)Errno.EPERM;
        }

        // Find the existing VmArea containing oldAddr
        var oldVma = Mem.FindVmArea(oldAddr);
        if (oldVma == null) return -(int)Errno.EFAULT;
        if (oldLen == 0 && (oldVma.Flags & MapFlags.Shared) == 0) return -(int)Errno.EINVAL;

        // Verify the old range is fully contained within the VMA
        if (oldAddr + oldLenAligned > oldVma.End) return -(int)Errno.EFAULT;

        // Case 1: Shrinking
        if (!isFixed && newLenAligned < oldLenAligned)
        {
            var unmapStart = oldAddr + newLenAligned;
            var unmapLen = oldLenAligned - newLenAligned;
            ProcessAddressSpaceSync.Munmap(Mem, engine, unmapStart, unmapLen);
            return (int)oldAddr;
        }

        // Case 2: Same size
        if (!isFixed && newLenAligned == oldLenAligned) return (int)oldAddr;

        // Case 3: Growing — try in place first
        var growLen = newLenAligned > oldLenAligned ? newLenAligned - oldLenAligned : 0;
        var growStart = oldAddr + oldLenAligned;

        // Check if there's free space right after the old region
        var canGrowInPlace = !isFixed && (ulong)oldAddr + newLenAligned <= taskSize &&
                             !Mem.TryGetVmaWindowForSyscall(growStart, growStart + growLen, out _, out _);

        if (canGrowInPlace)
        {
            if ((oldVma.Flags & MapFlags.Locked) != 0 &&
                engine.Owner is FiberTask lockTask && lockTask.Process != null &&
                lockTask.Process.EUID != 0 &&
                lockTask.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var memlockLimit))
            {
                if (memlockLimit.Soft == 0 || (ulong)Mem.GetTotalLockedVmBytes() + growLen > memlockLimit.Soft)
                    return -(int)Errno.ENOMEM;
            }

            // If oldAddr is at VmArea start and the old region covers the whole VmArea, extend it
            if (oldAddr == oldVma.Start && oldAddr + oldLenAligned == oldVma.End)
            {
                oldVma.End = oldAddr + newLenAligned;
                Mem.ResetVmAreaAttachmentsAfterResize(oldVma);
                if ((oldVma.Flags & MapFlags.Locked) != 0 && !oldVma.LockOnFault)
                {
                    if (Mem.LockRange(growStart, growLen, false, engine) < 0)
                        oldVma.Flags &= ~MapFlags.Locked;
                }
                return (int)oldAddr;
            }

            // For partial-range growth, map the appended slice with the same backing as oldVma.
            var growRc = TryMapRemapSlice(this, engine, oldVma, growStart, growLen, growStart);
            if (growRc == 0)
                return (int)oldAddr;
            if (growRc != -(int)Errno.ENOMEM)
                return growRc;
        }

        // Case 4: Need to move
        if (!mayMove) return -(int)Errno.ENOMEM;

        var copyLen = Math.Min(oldLenAligned, newLenAligned);
        var sourceOffset = ComputeSliceOffset(oldVma, oldAddr);

        var splitMiddle = oldAddr > oldVma.Start && oldAddr + oldLenAligned < oldVma.End;
        var neededVmas = 1 + (splitMiddle ? 1 : 0);
        if (!Mem.CanAllocateVmas(neededVmas))
            return -(int)Errno.ENOMEM;

        if (newLenAligned > oldLenAligned && (oldVma.Flags & MapFlags.Locked) != 0 &&
            engine.Owner is FiberTask moveLockTask && moveLockTask.Process != null &&
            moveLockTask.Process.EUID != 0 &&
            moveLockTask.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_MEMLOCK, out var moveLimit))
        {
            var delta = newLenAligned - oldLenAligned;
            if (moveLimit.Soft == 0 || (ulong)Mem.GetTotalLockedVmBytes() + delta > moveLimit.Soft)
                return -(int)Errno.ENOMEM;
        }

        // Find or use target address
        uint targetAddr;
        if (isFixed)
        {
            targetAddr = newAddr;
            // MREMAP_FIXED acts like MAP_FIXED — unmap anything at the target
            ProcessAddressSpaceSync.Munmap(Mem, engine, targetAddr, newLenAligned);
        }
        else
        {
            targetAddr = Mem.FindFreeRegion(newLenAligned);
            if (targetAddr == 0) return -(int)Errno.ENOMEM;
        }

        // Allocate new region
        var mapRc = TryMapRemapSlice(this, engine, oldVma, targetAddr, newLenAligned, oldAddr, sourceOffset);
        if (mapRc != 0)
            return mapRc;

        var targetVma = Mem.FindVmArea(targetAddr);
        if (targetVma != null)
        {
            Mem.MigrateVmaPages(oldVma, oldAddr, targetVma, targetAddr, copyLen, engine);
            if ((targetVma.Flags & MapFlags.Locked) != 0 && !targetVma.LockOnFault && newLenAligned > copyLen)
            {
                var tailStart = targetAddr + copyLen;
                if (Mem.LockRange(tailStart, newLenAligned - copyLen, false, engine) < 0)
                    targetVma.Flags &= ~MapFlags.Locked;
            }
        }

        // Unmap old region
        ProcessAddressSpaceSync.Munmap(Mem, engine, oldAddr, oldLenAligned);

        return (int)targetAddr;
    }

    private async ValueTask<int> SysRemapFilePages(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var size = a2;
        var prot = a3;
        var pgoff = a4;
        var flags = (int)a5;

        if (size == 0) return -(int)Errno.EINVAL;
        if ((addr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        if ((size & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        if (prot != 0) return -(int)Errno.EINVAL;
        if (flags != 0) return -(int)Errno.EINVAL;

        var taskSize = Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if ((ulong)addr + size > taskSize) return -(int)Errno.EINVAL;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);

        var end = addr + size;
        if (!Mem.TryGetContiguousVmaWindow(addr, end, out var startIndex, out var endIndexExclusive))
            return -(int)Errno.ENOMEM;

        var vmas = Mem.VMAs;
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            var v = vmas[i];
            if (!v.IsFileBacked || (v.Flags & MapFlags.Shared) == 0)
                return -(int)Errno.EINVAL;
        }

        var newOffset = (long)pgoff * LinuxConstants.PageSize;
        return ProcessAddressSpaceSync.RemapFileRange(Mem, engine, addr, size, newOffset, pgoff);
    }

    private static int TryMapRemapSlice(SyscallManager sm, Engine engine, VmArea sourceVma, uint targetAddr,
        uint length, uint sourceAddr, long? sourceOffset = null)
    {
        LinuxFile? clonedFile = null;
        try
        {
            clonedFile = CloneMappingFile(sourceVma);
            var offset = sourceOffset ?? ComputeSliceOffset(sourceVma, sourceAddr);
            var flags = (sourceVma.Flags | MapFlags.Fixed) & ~MapFlags.FixedNoReplace;

            _ = ProcessAddressSpaceSync.Mmap(sm.Mem, engine, targetAddr, length, sourceVma.Perms, flags,
                clonedFile, offset, sourceVma.Name);
            clonedFile = null; // ownership transferred to the new VmArea
            if (sm.Mem.FindVmArea(targetAddr) is { } targetVma)
            {
                if (!sourceVma.IsFileBacked)
                    targetVma.VmPgoff = sourceVma.GetPageIndex(sourceAddr);
                targetVma.DontFork = sourceVma.DontFork;
                targetVma.WipeOnFork = sourceVma.WipeOnFork;
                targetVma.LockOnFault = sourceVma.LockOnFault;
                targetVma.GrowDownLimit = sourceVma.GrowDownLimit;
                targetVma.GrowDownGuardGap = sourceVma.GrowDownGuardGap;
            }
            return 0;
        }
        catch (OutOfMemoryException)
        {
            return -(int)Errno.ENOMEM;
        }
        catch (ArgumentException)
        {
            return -(int)Errno.EINVAL;
        }
        catch (InvalidOperationException)
        {
            return -(int)Errno.ENOMEM;
        }
        finally
        {
            clonedFile?.Close();
        }
    }

    private static LinuxFile? CloneMappingFile(VmArea sourceVma)
    {
        var file = sourceVma.File;
        if (file == null) return null;
        file.Get();
        return file;
    }

    private static long ComputeSliceOffset(VmArea sourceVma, uint sourceAddr)
    {
        if (!sourceVma.IsFileBacked) return 0;
        return sourceVma.Offset + sourceVma.GetRelativeOffsetForAddress(sourceAddr);
    }
}
