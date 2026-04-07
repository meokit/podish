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
        if (newBrk == 0) return (int)BrkAddr;

        if (newBrk < BrkBase)
            return (int)BrkAddr;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        if (newBrk > BrkAddr)
        {
            var start = (BrkAddr + 0xFFF) & ~0xFFFu;
            var end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
                try
                {
                    // Map anonymous
                    ProcessAddressSpaceSync.Mmap(Mem, engine, start, end - start,
                        Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0,
                        "HEAP");
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

        if (len == 0) return -(int)Errno.EINVAL;

        var isNoReplace = (flags & (int)MapFlags.FixedNoReplace) != 0;
        var isFixed = (flags & (int)MapFlags.Fixed) != 0 || isNoReplace;
        if (addr != 0 && (addr & LinuxConstants.PageOffsetMask) != 0)
            return -(int)Errno.EINVAL;
        if (isFixed && addr == 0)
            return -(int)Errno.EINVAL;

        LinuxFile? f = null;
        LinuxFile? mmapFile = null;
        var isAnon = (flags & (int)MapFlags.Anonymous) != 0;
        var isShared = (flags & (int)MapFlags.Shared) != 0;
        var isPrivate = (flags & (int)MapFlags.Private) != 0;

        if (isShared == isPrivate)
            return -(int)Errno.EINVAL;

        var mapLen = (len + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
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
        else if (fd == -1)
        {
            return -(int)Errno.EBADF;
        }
        else
        {
            f = GetFD(fd);
            if (f == null) return -(int)Errno.EBADF;
            var inode = f.OpenedInode;
            if (inode == null) return -(int)Errno.EBADF;
            if (!inode.SupportsMmap) return -(int)Errno.ENODEV;
            if (isShared && (prot & (int)Protection.Write) != 0)
            {
                var canWrite = (f.Flags & (FileFlags.O_WRONLY | FileFlags.O_RDWR)) != 0;
                if (!canWrite) return -(int)Errno.EACCES;
            }
        }

        try
        {
            if (f != null)
                mmapFile = new LinuxFile(f.LivePath, f.Flags, LinuxFile.ReferenceKind.MmapHold, f.OpenedInode);

            var res = ProcessAddressSpaceSync.Mmap(Mem, engine, addr, len, (Protection)prot, (MapFlags)flags,
                mmapFile, offset, "MMAP2");
            mmapFile = null; // ownership transferred to VMAs
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
        ProcessAddressSpaceSync.Munmap(Mem, engine, a1, a2);
        return 0;
    }

    private async ValueTask<int> SysMprotect(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var addr = a1;
        var len = a2;
        var prot = (Protection)a3;

        if (len == 0) return 0;
        if ((addr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        return ProcessAddressSpaceSync.Mprotect(Mem, engine, addr, len, prot);
    }

    private async ValueTask<int> SysMadvise(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // No-op
    }

    private async ValueTask<int> SysMsync(Engine engine, uint addr, uint len, uint flags, uint a4, uint a5,
        uint a6)
    {
        ProcessAddressSpaceSync.SyncSharedRange(Mem, engine, addr, len);
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

        var mayMove = (flags & MREMAP_MAYMOVE) != 0;
        var isFixed = (flags & MREMAP_FIXED) != 0;

        if (isFixed && !mayMove) return -(int)Errno.EINVAL;
        if (isFixed && (newAddr & LinuxConstants.PageOffsetMask) != 0) return -(int)Errno.EINVAL;

        using var scope = ProcessAddressSpaceSync.EnterAddressSpaceScope(engine);
        // Align sizes to page boundaries
        var oldLenAligned = (oldLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;
        var newLenAligned = (newLen + LinuxConstants.PageOffsetMask) & ~LinuxConstants.PageOffsetMask;

        // Find the existing VmArea containing oldAddr
        var oldVma = Mem.FindVmArea(oldAddr);
        if (oldVma == null) return -(int)Errno.EFAULT;

        // Linux mremap operates on an existing mapping, not an arbitrary subrange inside a
        // larger VMA. Treat subrange remaps conservatively instead of mutating adjacent VMAs
        // into a shape the guest didn't actually own.
        if (oldAddr != oldVma.Start) return -(int)Errno.EINVAL;

        // Verify the old range is fully contained within the VMA
        if (oldAddr + oldLenAligned > oldVma.End) return -(int)Errno.EFAULT;

        // Case 1: Shrinking
        if (newLenAligned < oldLenAligned)
        {
            var unmapStart = oldAddr + newLenAligned;
            var unmapLen = oldLenAligned - newLenAligned;
            ProcessAddressSpaceSync.Munmap(Mem, engine, unmapStart, unmapLen);
            return (int)oldAddr;
        }

        // Case 2: Same size
        if (newLenAligned == oldLenAligned) return (int)oldAddr;

        // Case 3: Growing — try in place first
        var growLen = newLenAligned - oldLenAligned;
        var growStart = oldAddr + oldLenAligned;

        // Check if there's free space right after the old region
        var canGrowInPlace = true;
        Mem.VisitVmAreasInRange(growStart, growStart + growLen, v =>
        {
            if (v == oldVma) return; // ignore the VmArea itself if it extends past oldLen
            canGrowInPlace = false;
        });

        if (canGrowInPlace)
        {
            // If oldAddr is at VmArea start and the old region covers the whole VmArea, extend it
            if (oldAddr == oldVma.Start && oldAddr + oldLenAligned == oldVma.End)
            {
                oldVma.End = oldAddr + newLenAligned;
                ProcessAddressSpaceSync.PublishMappingChange(Mem, engine, growStart, growLen);
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
        var mapRc = TryMapRemapSlice(this, engine, oldVma, targetAddr, newLenAligned, oldAddr);
        if (mapRc != 0)
            return mapRc;

        var copyLen = Math.Min(oldLenAligned, newLenAligned);
        if (NeedsMoveCopy(oldVma, oldAddr, copyLen))
        {
            const int ChunkSize = 64 * 1024;
            var pool = ArrayPool<byte>.Shared;
            var buf = pool.Rent(ChunkSize);
            try
            {
                var remaining = copyLen;
                var currentOld = oldAddr;
                var currentTarget = targetAddr;

                while (remaining > 0)
                {
                    var toCopy = (int)Math.Min(remaining, ChunkSize);
                    if (!engine.CopyFromUser(currentOld, buf.AsSpan(0, toCopy)))
                    {
                        ProcessAddressSpaceSync.Munmap(Mem, engine, targetAddr, newLenAligned);
                        return -(int)Errno.EFAULT;
                    }

                    if (!engine.CopyToUser(currentTarget, buf.AsSpan(0, toCopy)))
                    {
                        ProcessAddressSpaceSync.Munmap(Mem, engine, targetAddr, newLenAligned);
                        return -(int)Errno.EFAULT;
                    }

                    currentOld += (uint)toCopy;
                    currentTarget += (uint)toCopy;
                    remaining -= (uint)toCopy;
                }
            }
            catch (OutOfMemoryException)
            {
                ProcessAddressSpaceSync.Munmap(Mem, engine, targetAddr, newLenAligned);
                return -(int)Errno.ENOMEM;
            }
            finally
            {
                pool.Return(buf);
            }
        }

        // Unmap old region
        ProcessAddressSpaceSync.Munmap(Mem, engine, oldAddr, oldLenAligned);

        return (int)targetAddr;
    }

    private static int TryMapRemapSlice(SyscallManager sm, Engine engine, VmArea sourceVma, uint targetAddr,
        uint length, uint sourceAddr)
    {
        LinuxFile? clonedFile = null;
        try
        {
            clonedFile = CloneMappingFile(sourceVma);
            var offset = ComputeSliceOffset(sourceVma, sourceAddr);
            var flags = (sourceVma.Flags | MapFlags.Fixed) & ~MapFlags.FixedNoReplace;

            _ = ProcessAddressSpaceSync.Mmap(sm.Mem, engine, targetAddr, length, sourceVma.Perms, flags,
                clonedFile, offset, sourceVma.Name);
            clonedFile = null; // ownership transferred to the new VmArea
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

    private static bool NeedsMoveCopy(VmArea sourceVma, uint sourceAddr, uint copyLen)
    {
        // Anonymous mappings have no stable backing store and must preserve bytes explicitly.
        if (!sourceVma.IsFileBacked) return true;

        // File-backed private mappings can be rebuilt from the shared source unless they
        // already carry process-private pages in their private object.
        var privateObject = sourceVma.VmAnonVma;
        if (privateObject == null || copyLen == 0) return false;

        var startPage = sourceVma.GetPageIndex(sourceAddr & LinuxConstants.PageMask);
        var pageCount = (copyLen + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
        for (uint i = 0; i < pageCount; i++)
            if (privateObject.PeekPage(startPage + i) != IntPtr.Zero)
                return true;

        return false;
    }
}