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
                parentTask?.HandleSignal(17); // SIGCHLD = 17
            }

            // If main thread exits, entire process group becomes zombie
            if (task.TID == task.Process.TGID)
            {
                ProcFsManager.OnProcessExit(sm, task.Process.TGID);
                task.Process.State = ProcessState.Zombie;
                task.Process.ExitStatus = exitCode;
                task.Process.ZombieEvent.Set();
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
                parentTask?.HandleSignal(17); // SIGCHLD = 17
            }

            ProcFsManager.OnProcessExit(sm, task.Process.TGID);
            task.Process.State = ProcessState.Zombie;
            task.Process.ExitStatus = exitCode;
            task.Process.ZombieEvent.Set();
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

        // TODO: Re-verify ProcFsManager compatibility
        // ProcFsManager.OnProcessStart(sm, child.TID);

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

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.ECHILD;

        var currentProc = fiberTask.Process;
        var kernel = KernelScheduler.Current!;

        // Loop for retrying wait
        while (true)
        {
            // Scan children
            var hasChildren = false;

            List<Process> candidates = [];

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
                            // Status word: bits 8-15 = exit status, bits 0-6 = signal (0 if exited)
                            // If signaled: bits 0-6 = signal, bit 7 = core dump
                            // We assume normal exit for now (childProc.ExitStatus is the code)
                            // TODO: If childProc was killed by signal, we need to store that differently
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf, (childProc.ExitStatus << 8) & 0xFF00);
                            if (!sm.Engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        // Reap
                        currentProc.Children.Remove(childPid);
                        // Also remove from global table? Or let it be garbage collected if no other refs?
                        // If we remove from global table, PID can be reused properly (if allocator reuses).
                        // Current allocator is monotonic increment.

                        return childPid;
                    }

                    candidates.Add(childProc);
                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;

            if (hang) return 0;

            // Block until ONE of candidates becomes zombie
            // Use TaskCompletionSource and register on all candidates
            var tcs = new TaskCompletionSource<bool>();

            void continuation()
            {
                tcs.TrySetResult(true);
            }

            foreach (var c in candidates) c.ZombieEvent.Register(continuation);

            fiberTask.RegisterBlockingSyscall(() =>
            {
                tcs.TrySetResult(false); // Interrupted
            });

            try
            {
                var success = await tcs.Task;
                if (!success)
                    // Return -ERESTARTSYS so HandleAsyncSyscall can handle SA_RESTART
                    return -(int)Errno.ERESTARTSYS;
            }
            finally
            {
                fiberTask.ClearInterrupt();
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
        // Other flags ignored for now
        if (options == 0) wexited = true; // Default?

        while (true)
        {
            List<Process> candidates = [];
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
                            var info = new SigInfo
                            {
                                si_signo = 17, // SIGCHLD
                                si_pid = childProc.TGID,
                                si_status = childProc.ExitStatus,
                                si_code = 1 // CLD_EXITED
                            };

                            if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
                        }

                        // Waitid keeps child unless WNOWAIT? 
                        // "waitid()... If WNOWAIT is set... leave the child in a waitable state"
                        var wnowait = ((int)options & 0x01000000) != 0;
                        if (!wnowait)
                            lock (currentProc.Children)
                            {
                                currentProc.Children.Remove(childPid);
                            }

                        return 0; // Success
                    }

                    candidates.Add(childProc);
                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;
            if (wnohang) return 0;

            // Block
            var tcs = new TaskCompletionSource<bool>();

            void continuation()
            {
                tcs.TrySetResult(true);
            }

            foreach (var c in candidates) c.ZombieEvent.Register(continuation);

            fiberTask.RegisterBlockingSyscall(() => { tcs.TrySetResult(false); });

            try
            {
                var success = await tcs.Task;
                if (!success)
                    // Return -ERESTARTSYS so HandleAsyncSyscall can handle SA_RESTART
                    return -(int)Errno.ERESTARTSYS;
            }
            finally
            {
                fiberTask.ClearInterrupt();
            }
        }
    }

    private static bool WriteSigInfo(SyscallManager sm, uint addr, SigInfo info)
    {
        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.si_signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.si_errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.si_code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.si_pid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), info.si_uid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.si_status);
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

        // Clear Memory
        sm.Mem.Clear(sm.Engine);

        // We also need to reset BRK
        sm.BrkAddr = 0; // ElfLoader will set it

        // Close O_CLOEXEC files
        var toClose = sm.FDs.Where(f => (f.Value.Flags & FileFlags.O_CLOEXEC) != 0).Select(f => f.Key).ToList();
        foreach (var fd in toClose) sm.FreeFD(fd);

        // Reset Signals (execve semantics: caught -> default, ignored -> kept)
        var ignored = task.Process.SignalActions
            .Where(kv => kv.Value.Handler == 1) // SIG_IGN = 1
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        task.Process.SignalActions.Clear();
        foreach (var kv in ignored) task.Process.SignalActions[kv.Key] = kv.Value;

        task.SignalMask = 0;
        task.PendingSignals = 0;
        task.AltStackSp = 0;
        task.AltStackSize = 0;
        task.AltStackFlags = 0;

        // Load new ELF
        Logger.LogInformation("[SysExecve] Loading {Path} with {ArgCount} args, {EnvCount} envs", absPath, args.Count,
            envs.Count);
        foreach (var arg in args) Logger.LogDebug("  arg: {Arg}", arg);
        try
        {
            // Note: ElfLoader.Load usually expects us to map the file.
            // In the current codebase usage (e.g. Program.cs), `ElfLoader.Load` takes a real file path?
            var res = ElfLoader.Load(absPath, sm, [.. args], [.. envs]);

            // Set CPU State
            sm.Engine.Eip = res.Entry;
            sm.Engine.RegWrite(Reg.ESP, res.SP);
            sm.Engine.Eflags = 0x202; // Reset EFLAGS like Program.cs

            // Reset segment bases (TLS will be re-setup by new process)
            sm.Engine.SetSegBase(Seg.GS, 0);
            sm.Engine.SetSegBase(Seg.FS, 0);

            // Reset other registers to clean state
            sm.Engine.RegWrite(Reg.EAX, 0);
            sm.Engine.RegWrite(Reg.EBX, 0);
            sm.Engine.RegWrite(Reg.ECX, 0);
            sm.Engine.RegWrite(Reg.EDX, 0);
            sm.Engine.RegWrite(Reg.ESI, 0);
            sm.Engine.RegWrite(Reg.EDI, 0);
            sm.Engine.RegWrite(Reg.EBP, 0);

            // Initial stack content must be written to memory
            // Use CopyToUser instead of MemWrite to avoid recursive fault handler
            var stackWritten = sm.Engine.CopyToUser(res.SP, res.InitialStack);
            Logger.LogDebug("[SysExecve] Stack write to 0x{SP:x} len={Len} success={Success}", res.SP,
                res.InitialStack.Length, stackWritten);
            if (!stackWritten)
            {
                Logger.LogError("[SysExecve] Failed to write initial stack!");
                return -(int)Errno.EFAULT;
            }

            sm.BrkAddr = res.BrkAddr; // Set BRK address from ElfLoader result

            return 0; // Success
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

        // Unreachable
        // return 0; 
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