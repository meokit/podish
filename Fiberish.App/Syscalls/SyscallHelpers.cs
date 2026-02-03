using System.Text;
using System.Linq;
using System.Threading;
using Bifrost.Core;
using Bifrost.VFS;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    // Callbacks for Task interaction
    public Func<int, uint, uint, uint, uint, (int, Exception?)>? CloneHandler { get; set; }
    public Action<Engine, int, bool>? ExitHandler { get; set; }
    public Func<Engine, int>? GetTID { get; set; }
    public Func<Engine, int>? GetTGID { get; set; }

    public string ReadString(uint addr)
    {
        if (addr == 0) return "";

        try
        {
            var sb = new StringBuilder();
            uint current = addr;
            while (true)
            {
                var b = Engine.MemRead(current++, 1)[0];
                if (b == 0) break;
                sb.Append((char)b);
                if (sb.Length > 4096) break; // Safety limit
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    public Dentry? PathWalk(string path, Dentry? startAt = null, bool followLink = true, int recursion = 0)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (recursion > 40) return null; // ELOOP

        // Console.WriteLine($"PathWalk: {path}");
        Dentry current;
        if (path.StartsWith("/"))
        {
            current = ProcessRoot;
        }
        else
        {
            current = startAt ?? CurrentWorkingDirectory;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (current == ProcessRoot) continue;

                if (current == current.SuperBlock.Root)
                {
                    if (current.MountedAt != null)
                    {
                        current = current.MountedAt;
                    }
                }

                if (current.Parent != null)
                {
                    current = current.Parent;
                }
                continue;
            }

            // Down
            // If current is a mount point, traverse into it
            if (current.IsMounted && current.MountRoot != null)
            {
                current = current.MountRoot;
            }

            Dentry? nextDentry = null;
            if (current.Children.TryGetValue(part, out var cached))
            {
                nextDentry = cached;
            }
            else
            {
                nextDentry = current.Inode!.Lookup(part);
                if (nextDentry == null) return null;

                current.Children[part] = nextDentry;
            }

            // Handle Symlink
            if (nextDentry.Inode!.Type == InodeType.Symlink && (followLink || i < parts.Length - 1))
            {
                string target = nextDentry.Inode.Readlink();
                var resolved = PathWalk(target, current, followLink, recursion + 1);
                if (resolved == null) return null;
                current = resolved;
            }
            else
            {
                current = nextDentry;
            }
        }

        if (current.IsMounted && current.MountRoot != null)
        {
            current = current.MountRoot;
        }

        return current;
    }

    public int AllocFD(Bifrost.VFS.File file)
    {
        int fd = 3;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = file;
        return fd;
    }

    public Bifrost.VFS.File? GetFD(int fd)
    {
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public void FreeFD(int fd)
    {
        if (FDs.Remove(fd, out var f))
        {
            f.Close();
        }
    }
}

// Wait syscall support
public enum IdType
{
    P_ALL = 0,    // Wait for any child
    P_PID = 1,    // Wait for specific PID
    P_PGID = 2    // Wait for process group (not implemented)
}

public class SigInfo
{
    public int si_signo;
    public int si_errno;
    public int si_code;
    public int si_pid;
    public int si_uid;
    public int si_status;
}

public static class WaitHelpers
{
    public const int WNOHANG = 1;
    public const int WUNTRACED = 2;
    public const int WCONTINUED = 8;
    public const int WEXITED = 4;
    public const int WSTOPPED = 2;

    // Core wait implementation - returns (result, tcs) where tcs is set if blocking is needed
    public static (int result, TaskCompletionSource<int>? tcs) KernelWaitId(Bifrost.Core.Task parentTask, IdType idtype, int id, SigInfo? infop, int options)
    {
        bool noHang = (options & WNOHANG) != 0;
        bool noWait = (options & 0x01000000) != 0; // WNOWAIT

        bool wantExited = (options & WEXITED) != 0;
        bool wantStopped = (options & (WSTOPPED | WUNTRACED)) != 0;
        bool wantContinued = (options & WCONTINUED) != 0;

        if ((options & (WEXITED | WSTOPPED | WUNTRACED | WCONTINUED)) == 0)
            wantExited = true;

        int parentTgid = parentTask.Process.TGID;
        Process? matchedChild = null;
        int matchCode = 0;

        List<int> childPids;
        lock (parentTask.Process.Children)
        {
            childPids = parentTask.Process.Children.ToList();
        }

        bool hasAnyChild = childPids.Count > 0;

        foreach (var childPid in childPids)
        {
            var proc = Scheduler.GetProcessByPID(childPid);
            if (proc == null) continue;

            // Check ID matching
            bool idMatch = false;
            if (idtype == IdType.P_ALL) idMatch = true;
            else if (idtype == IdType.P_PID && proc.TGID == id) idMatch = true;
            else if (idtype == IdType.P_PGID && proc.TGID == id) idMatch = true; // Simplified PGID

            if (!idMatch) continue;

            if (wantExited && proc.State == ProcessState.Zombie)
            {
                matchedChild = proc;
                matchCode = 1;
                break;
            }
            if (wantStopped && proc.State == ProcessState.Stopped)
            {
                matchedChild = proc;
                matchCode = 2;
                break;
            }
            if (wantContinued && proc.State == ProcessState.Continued)
            {
                matchedChild = proc;
                matchCode = 3;
                break;
            }
        }

        if (matchedChild != null)
        {
            if (infop != null)
            {
                infop.si_signo = 17; // SIGCHLD
                infop.si_pid = matchedChild.TGID;
                infop.si_uid = matchedChild.UID;
                infop.si_code = matchCode switch { 1 => 1, 2 => 4, 3 => 6, _ => 1 };
                infop.si_status = matchedChild.ExitStatus;
            }

            int childTgid = matchedChild.TGID;
            if (!noWait)
            {
                if (matchCode == 1)
                {
                    matchedChild.State = ProcessState.Dead;
                    Scheduler.RemoveProcess(childTgid);
                    lock (parentTask.Process.Children) parentTask.Process.Children.Remove(childTgid);
                }
                else
                {
                    matchedChild.State = ProcessState.Running;
                }
            }
            return (childTgid, null);
        }

        if (!hasAnyChild) return (-10, null); // ECHILD
        if (noHang) return (0, null);

        // Blocking wait
        var tcs = new TaskCompletionSource<int>();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            List<Process> children;
            lock (parentTask.Process.Children)
            {
                children = parentTask.Process.Children
                    .Select(pid => Scheduler.GetProcessByPID(pid))
                    .Where(p => p != null)
                    .ToList()!;
            }

            if (children.Count == 0) { tcs.SetResult(-10); return; }

            var waitHandles = children.Select(p => p.ZombieEvent.WaitHandle).ToArray();
            // Wait for ANY child to change state
            await System.Threading.Tasks.Task.Run(() => WaitHandle.WaitAny(waitHandles, 5000));

            // Just wake up the caller, don't reap here
            tcs.SetResult(0);
        });

        return (0, tcs);
    }
}
