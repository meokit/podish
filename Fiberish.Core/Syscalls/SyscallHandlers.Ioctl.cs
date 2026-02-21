using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysIoctl(IntPtr state, uint fd, uint request, uint arg, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;

        // Delegate to the inode's Ioctl method (polymorphic dispatch)
        var inode = file.Dentry.Inode;
        if (inode != null)
            return inode.Ioctl(request, arg, sm.Engine);

        return -(int)Errno.ENOTTY;
    }

    private static async ValueTask<int> SysFcntl64(IntPtr state, uint fd, uint cmd, uint arg, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        // Console.WriteLine($"[DEBUG] fcntl64({fd}, {cmd}, {arg})");

        if (!sm.FDs.ContainsKey((int)fd)) return -(int)Errno.EBADF;

        // Basic implementation for startup
        return cmd switch
        {
            // F_DUPFD
            0 => sm.AllocFD(sm.FDs[(int)fd], (int)arg),
            // F_GETFD
            1 => 0, // No flags
            // F_SETFD
            2 => 0, // Ignore FD_CLOEXEC for now
            // F_GETFL
            3 => (int)sm.FDs[(int)fd].Flags,
            // F_SETFL
            4 => 0, // Update flags (O_APPEND, O_NONBLOCK, etc)
            // Filter read-only flags
            // sm.FDs[(int)fd].Flags = (int)arg; 
            _ => -(int)Errno.EINVAL // Unimplemented fcntl64 cmd (suppress unless verbose)
        };
    }
}
