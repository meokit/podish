using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using System.Linq;
using Bifrost.Core;
using Bifrost.Native;
using Bifrost.Memory;
using Bifrost.VFS;
using Microsoft.Extensions.Logging;

namespace Bifrost.Syscalls;

public partial class SyscallManager
{
    private static async ValueTask<int> SysBrk(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint newBrk = a1;
        if (newBrk == 0) return (int)sm.BrkAddr;

        if (newBrk > sm.BrkAddr)
        {
            uint start = (sm.BrkAddr + 0xFFF) & ~0xFFFu;
            uint end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
            {
                // Map anonymous
                sm.Mem.Mmap(start, end - start, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "HEAP", sm.Engine);
            }
            sm.BrkAddr = newBrk;
        }
        return (int)sm.BrkAddr;
    }
    private static async ValueTask<int> SysMmap2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        uint addr = a1;
        uint len = a2;
        int prot = (int)a3;
        int flags = (int)a4;
        int fd = (int)a5;
        long offset = (long)a6 * 4096;

        prot |= (int)(Protection.Read | Protection.Write);

        Bifrost.VFS.File? f = null;
        bool isAnon = (flags & (int)MapFlags.Anonymous) != 0;

        if (!isAnon && fd != -1)
        {
            f = sm.GetFD(fd);
            if (f == null) return -(int)Errno.EBADF;
        }

        try
        {
            uint res = sm.Mem.Mmap(addr, len, (Protection)prot, (MapFlags)flags, f, offset, len, "MMAP2", sm.Engine);
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
        sm.Engine.InvalidateRange(a1, a2);
        sm.Mem.Munmap(a1, a2, sm.Engine);
        return 0;
    }
    private static async ValueTask<int> SysMprotect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        uint addr = a1;
        uint len = a2;
        Protection prot = (Protection)a3;

        // Invalidate cache since permissions are changing (e.g. RW -> RX)
        sm.Engine.InvalidateRange(addr, len);

        var vmas = sm.Mem.FindVMAsInRange(addr, addr + len);
        foreach (var vma in vmas)
        {
            // Update permissions in VMA manager
            vma.Perms = prot;

            // Update permissions in native MMU for already mapped pages
            for (uint p = Math.Max(vma.Start, addr); p < Math.Min(vma.End, addr + len); p += 4096)
            {
                if (sm.Engine.IsDirty(p)) // Check if mapped/present using a proxy
                {
                    // Actually we need a way to update native perms without re-mapping?
                    // For now, MemMap will update perms in native MMU
                    sm.Engine.MemMap(p, 4096, (byte)prot);
                }
            }
        }

        return 0;
    }
    private static async ValueTask<int> SysMadvise(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // No-op
    }
    private static async ValueTask<int> SysMsync(IntPtr state, uint addr, uint len, uint flags, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint end = addr + len;
        foreach (var vma in sm.Mem.FindVMAsInRange(addr, end))
        {
            VMAManager.SyncVMA(vma, sm.Engine);
        }
        return 0;
    }
}
