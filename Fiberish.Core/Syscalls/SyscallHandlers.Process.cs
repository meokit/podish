using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Loader;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using File = System.IO.File;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysExit(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var exitCode = (int)a1;
        if (sm.Engine.Owner is FiberTask task)
        {
            task.Exited = true;
            task.ExitStatus = exitCode;

            // Notify Parent
            var ppid = task.Process.PPID;
            if (ppid > 0)
            {
                var parentTask = task.CommonKernel.GetTask(ppid);
                parentTask?.PostSignal(17); // SIGCHLD = 17
            }

            // If main thread exits, entire process group becomes zombie
            if (task.TID == task.Process.TGID)
            {
                ProcFsManager.OnProcessExit(sm, task.Process.TGID);
                sm.SysVShm.OnProcessExit(task.Process.TGID, sm.Mem, sm.Engine);
                task.Process.State = ProcessState.Zombie;
                task.Process.ExitStatus = exitCode;
                task.Process.ExitedBySignal = false;
                task.Process.TermSignal = 0;
                task.Process.CoreDumped = false;
                task.Process.HasWaitableStop = false;
                task.Process.HasWaitableContinue = false;
                task.Process.StateChangeEvent.Set();
            }
        }

        sm.ExitHandler?.Invoke(sm.Engine, exitCode, false);
        sm.Engine.Stop();
        return 0;
    }

    private static async ValueTask<int> SysExitGroup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var exitCode = (int)a1;
        if (sm.Engine.Owner is FiberTask task)
        {
            task.Exited = true;
            task.ExitStatus = exitCode;

            // Notify Parent
            var ppid = task.Process.PPID;
            if (ppid > 0)
            {
                var parentTask = task.CommonKernel.GetTask(ppid);
                parentTask?.PostSignal(17); // SIGCHLD = 17
            }

            ProcFsManager.OnProcessExit(sm, task.Process.TGID);
            sm.SysVShm.OnProcessExit(task.Process.TGID, sm.Mem, sm.Engine);
            task.Process.State = ProcessState.Zombie;
            task.Process.ExitStatus = exitCode;
            task.Process.ExitedBySignal = false;
            task.Process.TermSignal = 0;
            task.Process.CoreDumped = false;
            task.Process.HasWaitableStop = false;
            task.Process.HasWaitableContinue = false;
            task.Process.StateChangeEvent.Set();
        }

        sm.ExitHandler?.Invoke(sm.Engine, exitCode, true);
        sm.Engine.Stop();
        return 0;
    }

    private static async ValueTask<int> SysClone(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (sm.Engine.Owner is not FiberTask current) return -(int)Errno.EPERM;

        var flags = a1;
        var stackPtr = a2;
        var ptidPtr = a3;
        var tlsPtr = a4;
        var ctidPtr = a5;

        // Clone
        var child = await current.Clone((int)flags, stackPtr, ptidPtr, tlsPtr, ctidPtr);

        if ((flags & LinuxConstants.CLONE_THREAD) == 0)
            ProcFsManager.OnProcessStart(child.Process.Syscalls, child.Process);

        return child.TID;
    }

    private static async ValueTask<int> SysFork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // fork = clone(0, 0, NULL, NULL, NULL) - no flags, copy everything
        return await SysClone(state, 0, 0, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysVfork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // vfork = clone(CLONE_VM | CLONE_VFORK, 0, NULL, NULL, NULL)
        return await SysClone(state, LinuxConstants.CLONE_VM | LinuxConstants.CLONE_VFORK, 0, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysWait4(IntPtr state, uint pid, uint statusPtr, uint options, uint rusagePtr,
        uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var pidVal = (int)pid;
        var optVal = (int)options;
        var hang = (optVal & 1) != 0; // WNOHANG
        var wantStopped = (optVal & 2) != 0; // WUNTRACED/WSTOPPED
        var wantContinued = (optVal & 8) != 0; // WCONTINUED
        var noReap = (optVal & 0x01000000) != 0; // WNOWAIT - report but don't reap

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.ECHILD;

        var currentProc = fiberTask.Process;
        var kernel = KernelScheduler.Current!;

        // Loop for retrying wait
        while (true)
        {
            // Scan children
            var hasChildren = false;

            // Iterate over copy to allow modification if needed? No, removing from dict is what matters.
            // currentProc.Children is List<int>.
            // We can iterate it directly.

            foreach (var childPid in currentProc.Children)
            {
                var childProc = kernel.GetProcess(childPid);
                if (childProc == null) continue;

                var match = false;
                if (pidVal == -1) match = true;
                else if (pidVal > 0) match = childPid == pidVal;
                else if (pidVal == 0) match = childProc.PGID == currentProc.PGID;
                else match = childProc.PGID == -pidVal;

                if (match)
                {
                    hasChildren = true;
                    if (childProc.State == ProcessState.Zombie)
                    {
                        // Found REAPABLE child
                        if (statusPtr != 0)
                        {
                            var stBuf = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf, EncodeWaitExitStatus(childProc));
                            if (!sm.Engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        // Reap only if WNOWAIT is not set
                        if (!noReap)
                        {
                            currentProc.Children.Remove(childPid);
                            // Also remove from global table? Or let it be garbage collected if no other refs?
                            // If we remove from global table, PID can be reused properly (if allocator reuses).
                            // Current allocator is monotonic increment.
                        }

                        return childPid;
                    }
                    
                    if (wantStopped && childProc.State == ProcessState.Stopped && childProc.HasWaitableStop)
                    {
                        if (statusPtr != 0)
                        {
                            var stBuf = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf, EncodeWaitStoppedStatus(childProc.StopSignal));
                            if (!sm.Engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        childProc.HasWaitableStop = false;
                        return childPid;
                    }

                    if (wantContinued && childProc.HasWaitableContinue)
                    {
                        if (statusPtr != 0)
                        {
                            var stBuf = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf, 0xffff);
                            if (!sm.Engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        childProc.HasWaitableContinue = false;
                        return childPid;
                    }

                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;

            if (hang) return 0;

            await new ChildStateAwaitable(currentProc, pidVal);
            if (fiberTask.HasUnblockedPendingSignal())
            {
                // Ignored/default-ignored signals (e.g. SIGCHLD/SIGWINCH) should wake wait*
                // and let it re-scan children, not force EINTR.
                if (!HasPendingInterruptingSignal(fiberTask)) continue;
                return -(int)Errno.ERESTARTSYS;
            }
        }
    }

    private static async ValueTask<int> SysWaitPid(IntPtr state, uint pid, uint statusPtr, uint options, uint a4,
        uint a5, uint a6)
    {
        // waitpid(pid, status, options) = wait4(pid, status, options, NULL)
        return await SysWait4(state, pid, statusPtr, options, 0, 0, 0);
    }

    private static async ValueTask<int> SysWaitId(IntPtr state, uint idtype, uint id, uint infop, uint options,
        uint rusagePtr, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.ECHILD;

        // Logic similar to SysWait4 but with idtype
        var currentProc = fiberTask.Process;
        var kernel = KernelScheduler.Current!;

        var wnohang = ((int)options & 1) != 0;
        var wexited = ((int)options & 4) != 0;
        var wstopped = ((int)options & 2) != 0;
        var wcontinued = ((int)options & 8) != 0;
        var wnowait = ((int)options & 0x01000000) != 0;
        if (options == 0) wexited = true; // Default?

        while (true)
        {
            var hasChildren = false;

            foreach (var childPid in currentProc.Children)
            {
                var childProc = kernel.GetProcess(childPid);
                if (childProc == null) continue;

                var match = false;
                if ((IdType)idtype == IdType.P_ALL) match = true;
                else if ((IdType)idtype == IdType.P_PID && childPid == (int)id) match = true;
                else if ((IdType)idtype == IdType.P_PGID && childProc.PGID == (int)id) match = true;

                if (match)
                {
                    hasChildren = true;
                    if (wexited && childProc.State == ProcessState.Zombie)
                    {
                        // Found
                        if (infop != 0)
                        {
                            var info = BuildSigchldInfoForExit(childProc);

                            if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
                        }

                        if (!wnowait)
                            lock (currentProc.Children)
                            {
                                currentProc.Children.Remove(childPid);
                            }

                        return 0; // Success
                    }
                    
                    if (wstopped && childProc.State == ProcessState.Stopped && childProc.HasWaitableStop)
                    {
                        if (infop != 0)
                        {
                            var info = new SigInfo
                            {
                                Signo = (int)Signal.SIGCHLD,
                                Pid = childProc.TGID,
                                Status = childProc.StopSignal,
                                Code = 5 // CLD_STOPPED
                            };
                            if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
                        }

                        if (!wnowait) childProc.HasWaitableStop = false;
                        return 0;
                    }

                    if (wcontinued && childProc.HasWaitableContinue)
                    {
                        if (infop != 0)
                        {
                            var info = new SigInfo
                            {
                                Signo = (int)Signal.SIGCHLD,
                                Pid = childProc.TGID,
                                Status = (int)Signal.SIGCONT,
                                Code = 6 // CLD_CONTINUED
                            };
                            if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
                        }

                        if (!wnowait) childProc.HasWaitableContinue = false;
                        return 0;
                    }

                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;
            if (wnohang) return 0;

            await new ChildStateAwaitable(currentProc, (int)id);
            if (fiberTask.HasUnblockedPendingSignal())
            {
                // Ignored/default-ignored signals (e.g. SIGCHLD/SIGWINCH) should wake wait*
                // and let it re-scan children, not force EINTR.
                if (!HasPendingInterruptingSignal(fiberTask)) continue;
                return -(int)Errno.ERESTARTSYS;
            }
        }
    }

    private static bool HasPendingInterruptingSignal(FiberTask task)
    {
        lock (task)
        {
            var pending = task.PendingSignals;
            var unblocked = pending & ~task.SignalMask;

            // SIGKILL/SIGSTOP are unmaskable.
            var unmaskable = (1UL << ((int)Signal.SIGKILL - 1)) | (1UL << ((int)Signal.SIGSTOP - 1));
            unblocked |= pending & unmaskable;

            if (unblocked == 0) return false;

            for (var sig = 1; sig <= 64; sig++)
            {
                var mask = 1UL << (sig - 1);
                if ((unblocked & mask) == 0) continue;

                if (task.Process.SignalActions.TryGetValue(sig, out var action))
                {
                    if (action.Handler == 1) continue; // SIG_IGN
                    if (action.Handler == 0 && IsDefaultIgnoredSignal(sig)) continue;
                    return true;
                }

                if (!IsDefaultIgnoredSignal(sig)) return true;
            }

            return false;
        }
    }

    private static bool IsDefaultIgnoredSignal(int sig)
    {
        return sig == (int)Signal.SIGCHLD ||
               sig == (int)Signal.SIGURG ||
               sig == (int)Signal.SIGWINCH;
    }

    private static int EncodeWaitExitStatus(Process childProc)
    {
        if (childProc.ExitedBySignal)
        {
            var status = childProc.TermSignal & 0x7f;
            if (childProc.CoreDumped) status |= 0x80;
            return status;
        }

        return (childProc.ExitStatus & 0xff) << 8;
    }

    private static int EncodeWaitStoppedStatus(int stopSignal)
    {
        return 0x7f | ((stopSignal & 0xff) << 8);
    }

    private static SigInfo BuildSigchldInfoForExit(Process childProc)
    {
        if (!childProc.ExitedBySignal)
        {
            return new SigInfo
            {
                Signo = (int)Signal.SIGCHLD,
                Pid = childProc.TGID,
                Status = childProc.ExitStatus,
                Code = 1 // CLD_EXITED
            };
        }

        return new SigInfo
        {
            Signo = (int)Signal.SIGCHLD,
            Pid = childProc.TGID,
            Status = childProc.TermSignal,
            Code = childProc.CoreDumped ? 3 : 2 // CLD_DUMPED / CLD_KILLED
        };
    }

    private static bool WriteSigInfo(SyscallManager sm, uint addr, SigInfo info)
    {
        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.Signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.Errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.Code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.Pid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), info.Uid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.Status);
        return sm.Engine.CopyToUser(addr, buf);
    }

    private static async ValueTask<int> SysExecve(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        Logger.LogDebug(
            "[SysExecve] sm.Engine==task.CPU? {Same}, sm.Engine.State=0x{EngState:x}, task.CPU.State=0x{CpuState:x}",
            ReferenceEquals(sm.Engine, task.CPU), sm.Engine.State, task.CPU.State);

        var filename = sm.ReadString(a1);
        if (string.IsNullOrEmpty(filename)) return -(int)Errno.EFAULT;
        // Resolve path via VFS
        var dentry = sm.PathWalk(filename);
        string? hostPath = null;

        if (dentry?.Inode is HostInode hi)
            hostPath = hi.HostPath;
        else if (dentry?.Inode is OverlayInode oi && oi.UpperInode == null && oi.LowerInode is HostInode lhi)
            hostPath = lhi.HostPath;

        if (hostPath == null)
        {
            Logger.LogDebug("[SysExecve] Could not resolve '{Filename}' to a host-backed file in VFS", filename);
            return -(int)Errno.ENOENT;
        }

        if (!File.Exists(hostPath))
        {
            Logger.LogWarning("[SysExecve] VFS resolved '{Filename}' to '{HostPath}', but file does not exist on host",
                filename, hostPath);
            return -(int)Errno.ENOENT;
        }

        // We use the host path for loading
        var absPath = hostPath;

        // Read Args (must be done BEFORE clearing memory)
        List<string> args = [];
        if (a2 != 0)
        {
            var curr = a2;
            var ptrBuf = new byte[4];
            while (true)
            {
                if (!sm.Engine.CopyFromUser(curr, ptrBuf)) break;
                var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                if (strPtr == 0) break;
                args.Add(sm.ReadString(strPtr));
                curr += 4;
            }
        }

        // Read Envs (must be done BEFORE clearing memory)
        List<string> envs = [];
        if (a3 != 0)
        {
            var curr = a3;
            var ptrBuf = new byte[4];
            while (true)
            {
                if (!sm.Engine.CopyFromUser(curr, ptrBuf)) break;
                var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                if (strPtr == 0) break;
                envs.Add(sm.ReadString(strPtr));
                curr += 4;
            }
        }

        // Close O_CLOEXEC files
        var toClose = sm.FDs.Where(f => (f.Value.Flags & FileFlags.O_CLOEXEC) != 0).Select(f => f.Key).ToList();
        foreach (var fd in toClose) sm.FreeFD(fd);

        // Reset Signals (execve semantics: caught -> default, ignored -> kept)
        var ignored = task.Process.SignalActions
            .Where(kv => kv.Value.Handler == 1) // SIG_IGN = 1
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        task.Process.SignalActions.Clear();
        foreach (var kv in ignored) task.Process.SignalActions[kv.Key] = kv.Value;

        // SignalMask is preserved across execve
        task.PendingSignals = 0;
        task.AltStackSp = 0;
        task.AltStackSize = 0;
        task.AltStackFlags = 0;

        // Load new ELF via Process.Exec (Handles memory clearing + vDSO setup + ELF loading)
        Logger.LogInformation("[SysExecve] Loading {Path} with {ArgCount} args, {EnvCount} envs via Process.Exec", absPath, args.Count,
            envs.Count);
        foreach (var arg in args) Logger.LogDebug("  arg: {Arg}", arg);
        
        try
        {
            task.Process.Exec(absPath, [.. args], [.. envs]);
            ProcFsManager.OnProcessExec(sm, task.Process);
            return 0; // Success (Task continues at new EIP set by LoadExecutable)
        }
        catch (FileNotFoundException)
        {
            return -(int)Errno.ENOENT;
        }
        catch (Exception ex) // Catch other exceptions during execve
        {
            Logger.LogWarning("Execve failed: {Message}", ex.Message);
            return -(int)Errno.ENOENT;
        } 
    }

    private static async ValueTask<int> SysSetSid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // setsid()
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var proc = task.Process;

        // If caller is a process group leader, return EPERM
        if (proc.PGID == proc.TGID) return -(int)Errno.EPERM;

        proc.SID = proc.TGID;
        proc.PGID = proc.TGID;

        // Detach CTTY implies we are no longer associated with TTY's session?
        // In our simple model, TtyDiscipline.SessionId holds the active session.
        // If we were part of it, we are not anymore.

        return proc.SID;
    }

    private static async ValueTask<int> SysSetPgid(IntPtr state, uint pid, uint pgid, uint a3, uint a4, uint a5,
        uint a6)
    {
        // setpgid(pid, pgid)
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var kernel = task.CommonKernel;
        var targetPid = (int)pid == 0 ? task.Process.TGID : (int)pid;
        var targetPgid = (int)pgid == 0 ? targetPid : (int)pgid;

        var targetProc = kernel.GetProcess(targetPid);
        if (targetProc == null) return -(int)Errno.ESRCH;

        // Permission checks
        // 1. Target must be calling process or child of calling process
        if (targetProc.TGID != task.Process.TGID && targetProc.PPID != task.Process.TGID)
            return -(int)Errno.EPERM;

        // 2. Target must be in same session
        if (targetProc.SID != task.Process.SID) return -(int)Errno.EPERM;

        // 3. Cannot change if session leader
        if (targetProc.SID == targetProc.TGID) return -(int)Errno.EPERM;

        // TODO: Check if child has already exec'd (should return EACCES)

        targetProc.PGID = targetPgid;
        return 0;
    }

    private static async ValueTask<int> SysGetSid(IntPtr state, uint pid, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var kernel = KernelScheduler.Current!;

        var targetPid = (int)pid == 0 ? (sm.Engine.Owner as FiberTask)!.Process.TGID : (int)pid;
        var targetProc = kernel.GetProcess(targetPid);
        if (targetProc == null) return -(int)Errno.ESRCH;

        return targetProc.SID;
    }

    private static async ValueTask<int> SysGetPgrp(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        return task.Process.PGID;
    }
}
