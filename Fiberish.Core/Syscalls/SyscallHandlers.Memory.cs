using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysBrk(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var newBrk = a1;
        if (newBrk == 0) return (int)sm.BrkAddr;

        if (newBrk < sm.BrkBase)
            return (int)sm.BrkAddr;

        if (newBrk > sm.BrkAddr)
        {
            var start = (sm.BrkAddr + 0xFFF) & ~0xFFFu;
            var end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
            {
                try
                {
                    // Map anonymous
                    sm.Mem.Mmap(start, end - start, Protection.Read | Protection.Write,
                        MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "HEAP", sm.Engine);
                }
                catch
                {
                    return (int)sm.BrkAddr;
                }
            }
            sm.BrkAddr = newBrk;
            return (int)sm.BrkAddr;
        }

        if (newBrk < sm.BrkAddr)
        {
            var start = (newBrk + 0xFFF) & ~0xFFFu;
            var end = (sm.BrkAddr + 0xFFF) & ~0xFFFu;
            if (end > start)
                sm.Mem.Munmap(start, end - start, sm.Engine);
            sm.BrkAddr = newBrk;
        }

        return (int)sm.BrkAddr;
    }

    private static async ValueTask<int> SysMmap2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var addr = a1;
        var len = a2;
        var prot = (int)a3;
        var flags = (int)a4;
        var fd = (int)a5;
        var offset = (long)a6 * 4096;

        if (len == 0) return -(int)Errno.EINVAL;

        var isNoReplace = (flags & (int)MapFlags.FixedNoReplace) != 0;
        var isFixed = (flags & (int)MapFlags.Fixed) != 0 || isNoReplace;
        if (addr != 0 && (addr & LinuxConstants.PageOffsetMask) != 0)
            return -(int)Errno.EINVAL;
        if (isFixed && addr == 0)
            return -(int)Errno.EINVAL;

        VFS.LinuxFile? f = null;
        var isAnon = (flags & (int)MapFlags.Anonymous) != 0;
        var isShared = (flags & (int)MapFlags.Shared) != 0;
        var isPrivate = (flags & (int)MapFlags.Private) != 0;

        if (isShared == isPrivate)
            return -(int)Errno.EINVAL;

        var mapLen = (len + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        if (isNoReplace && sm.Mem.FindVMAsInRange(addr, addr + mapLen).Count > 0)
            return -(int)Errno.EEXIST;

        if (isAnon)
        {
            fd = -1;
            offset = 0;
        }
        else if (fd == -1)
        {
            return -(int)Errno.EBADF;
        }
        else
        {
            f = sm.GetFD(fd);
            if (f == null) return -(int)Errno.EBADF;
            if (isShared && (prot & (int)Protection.Write) != 0)
            {
                var canWrite = (f.Flags & (VFS.FileFlags.O_WRONLY | VFS.FileFlags.O_RDWR)) != 0;
                if (!canWrite) return -(int)Errno.EACCES;
            }
        }

        try
        {
            var res = sm.Mem.Mmap(addr, len, (Protection)prot, (MapFlags)flags, f, offset, len, "MMAP2", sm.Engine);
            return (int)res;
        }
        catch
        {
            return -(int)Errno.ENOMEM;
        }
    }

    private static async ValueTask<int> SysMunmap(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (a2 == 0) return -(int)Errno.EINVAL;
        if ((a1 & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;
        sm.Mem.Munmap(a1, a2, sm.Engine);
        return 0;
    }

    private static async ValueTask<int> SysMprotect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var addr = a1;
        var len = a2;
        var prot = (Protection)a3;

        if (len == 0) return 0;
        if ((addr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        // Invalidate cache since permissions are changing (e.g. RW -> RX)
        sm.Engine.InvalidateRange(addr, len);

        var end = addr + len;
        var vmas = sm.Mem.FindVMAsInRange(addr, end);
        if (vmas.Count == 0) return -(int)Errno.ENOMEM;

        vmas.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = addr;
        foreach (var vma in vmas)
        {
            if (vma.Start > cursor) return -(int)Errno.ENOMEM;
            if (vma.End > cursor) cursor = vma.End;
            if (cursor >= end) break;
        }
        if (cursor < end) return -(int)Errno.ENOMEM;

        foreach (var vma in vmas)
        {
            // Update permissions in VMA manager
            vma.Perms = prot;

            // Update permissions in native MMU for already mapped pages
            for (var p = Math.Max(vma.Start, addr); p < Math.Min(vma.End, addr + len); p += 4096)
                if (sm.Engine.IsDirty(p)) // Check if mapped/present using a proxy
                    // Actually we need a way to update native perms without re-mapping?
                    // For now, MemMap will update perms in native MMU
                    sm.Engine.MemMap(p, 4096, (byte)prot);
        }

        return 0;
    }

    private static async ValueTask<int> SysMadvise(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // No-op
    }

    private static async ValueTask<int> SysMsync(IntPtr state, uint addr, uint len, uint flags, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var end = addr + len;
        foreach (var vma in sm.Mem.FindVMAsInRange(addr, end)) VMAManager.SyncVMA(vma, sm.Engine);
        return 0;
    }

    private static async ValueTask<int> SysMremap(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // void *mremap(void *old_address, size_t old_size, size_t new_size, int flags, ... void *new_address);
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldAddr = a1;
        var oldLen = a2;
        var newLen = a3;
        var flags = (int)a4;
        var newAddr = a5;

        const int MREMAP_MAYMOVE = 1;
        const int MREMAP_FIXED = 2;

        if (newLen == 0) return -(int)Errno.EINVAL;
        if ((oldAddr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        var mayMove = (flags & MREMAP_MAYMOVE) != 0;
        var isFixed = (flags & MREMAP_FIXED) != 0;

        if (isFixed && !mayMove) return -(int)Errno.EINVAL;
        if (isFixed && (newAddr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        // Align sizes to page boundaries
        var oldLenAligned = (oldLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        var newLenAligned = (newLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;

        // Find the existing VMA containing oldAddr
        var oldVma = sm.Mem.FindVMA(oldAddr);
        if (oldVma == null) return -(int)Errno.EFAULT;

        // Verify the old range is fully contained within the VMA
        if (oldAddr + oldLenAligned > oldVma.End) return -(int)Errno.EFAULT;

        // Case 1: Shrinking
        if (newLenAligned < oldLenAligned)
        {
            // Unmap the tail portion
            if (newLenAligned < oldLenAligned)
            {
                var unmapStart = oldAddr + newLenAligned;
                var unmapLen = oldLenAligned - newLenAligned;
                sm.Mem.Munmap(unmapStart, unmapLen, sm.Engine);
            }
            return (int)oldAddr;
        }

        // Case 2: Same size
        if (newLenAligned == oldLenAligned) return (int)oldAddr;

        // Case 3: Growing — try in place first
        var growLen = newLenAligned - oldLenAligned;
        var growStart = oldAddr + oldLenAligned;

        // Check if there's free space right after the old region
        var canGrowInPlace = true;
        var nextVmas = sm.Mem.FindVMAsInRange(growStart, growStart + growLen);
        foreach (var v in nextVmas)
        {
            if (v != oldVma) // ignore the VMA itself if it extends past oldLen
            {
                canGrowInPlace = false;
                break;
            }
        }

        if (canGrowInPlace)
        {
            // If oldAddr is at VMA start and the old region covers the whole VMA, extend it
            if (oldAddr == oldVma.Start && oldAddr + oldLenAligned == oldVma.End)
            {
                oldVma.End = oldAddr + newLenAligned;
                return (int)oldAddr;
            }

            // Otherwise we need to create a new anonymous VMA for the growth region
            // and extend via a new mapping
            try
            {
                sm.Mem.Mmap(growStart, growLen, oldVma.Perms,
                    MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed,
                    null, 0, 0, oldVma.Name, sm.Engine);
                return (int)oldAddr;
            }
            catch
            {
                // Fall through to move case
            }
        }

        // Case 4: Need to move
        if (!mayMove) return -(int)Errno.ENOMEM;

        // Find or use target address
        uint targetAddr;
        if (isFixed)
        {
            targetAddr = newAddr;
            // MREMAP_FIXED acts like MAP_FIXED — unmap anything at the target
            sm.Mem.Munmap(targetAddr, newLenAligned, sm.Engine);
        }
        else
        {
            targetAddr = sm.Mem.FindFreeRegion(newLenAligned);
            if (targetAddr == 0) return -(int)Errno.ENOMEM;
        }

        try
        {
            // Allocate new region
            sm.Mem.Mmap(targetAddr, newLenAligned, oldVma.Perms,
                MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed,
                null, 0, 0, oldVma.Name, sm.Engine);

            // Copy old content to new location
            var copyLen = Math.Min(oldLenAligned, newLenAligned);
            var buf = new byte[copyLen];
            sm.Engine.CopyFromUser(oldAddr, buf);
            sm.Engine.CopyToUser(targetAddr, buf);

            // Unmap old region
            sm.Mem.Munmap(oldAddr, oldLenAligned, sm.Engine);

            return (int)targetAddr;
        }
        catch
        {
            return -(int)Errno.ENOMEM;
        }
    }
}
