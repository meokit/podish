using System;
using Bifrost.Core;
using Bifrost.Native;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    private static int SysPoll(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return SyscallAsync.Poll(sm, a1, a2, (int)a3);
    }
    
    private static int SysSelect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        // old_select(struct sel_arg_struct *)
        uint argsAddr = a1;
        if (argsAddr == 0) return -(int)Errno.EFAULT;

        byte[] args = new byte[20];
        if (!sm.Engine.CopyFromUser(argsAddr, args)) return -(int)Errno.EFAULT;
        int n = System.BitConverter.ToInt32(args, 0);
        uint inp = System.BitConverter.ToUInt32(args, 4);
        uint outp = System.BitConverter.ToUInt32(args, 8);
        uint exp = System.BitConverter.ToUInt32(args, 12);
        uint tvp = System.BitConverter.ToUInt32(args, 16);

        return SyscallAsync.Select(sm, n, inp, outp, exp, tvp);
    }

    private static int SysNewSelect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return SyscallAsync.Select(sm, (int)a1, a2, a3, a4, a5);
    }
}
