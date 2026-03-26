using System.Buffers.Binary;
using System.Text;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysExit(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var exitCode = (int)a1;
        if (engine.Owner is FiberTask task)
        {
            // vfork: wake parent before exiting
            task.SignalVforkDone();

            task.ExitRobustList();
            task.ClearChildTidAndWake();

            task.Exited = true;
            task.ExitStatus = exitCode;
            var remainingThreads = task.CommonKernel.DetachTask(task);
            if (remainingThreads == 0)
                FinalizeProcessExit(task, exitCode, false, 0, false);
        }

        ExitHandler?.Invoke(engine, exitCode, false);
        engine.Stop();
        return 0;
    }

    private async ValueTask<int> SysExitGroup(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var exitCode = (int)a1;
        if (engine.Owner is FiberTask task)
        {
            // vfork: wake parent before exiting
            task.SignalVforkDone();

            task.ExitRobustList();
            task.ClearChildTidAndWake();

            task.Exited = true;
            task.ExitStatus = exitCode;
            FinalizeProcessExit(task, exitCode, false, 0, false);
        }

        ExitHandler?.Invoke(engine, exitCode, true);
        engine.Stop();
        return 0;
    }

    private async ValueTask<int> SysClone(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask current) return -(int)Errno.EPERM;

        var flags = a1;
        var stackPtr = a2;
        var ptidPtr = a3;
        // x86 clone(2) raw syscall argument order:
        // clone(flags, child_stack, ptid, tls, ctid)
        var tlsPtr = a4;
        var ctidPtr = a5;

        var flagError = ValidateCloneFlags(flags);
        if (flagError != 0) return flagError;

        FiberTask child;
        try
        {
            child = await current.Clone((int)flags, stackPtr, ptidPtr, tlsPtr, ctidPtr);
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
            return -(int)Errno.EFAULT;
        }

        if ((flags & LinuxConstants.CLONE_THREAD) == 0)
            ProcFsManager.OnProcessStart(child.Process.Syscalls, child.Process);

        return child.TID;
    }

    private static int ValidateCloneFlags(uint flags)
    {
        var cloneVm = (flags & LinuxConstants.CLONE_VM) != 0;
        var cloneThread = (flags & LinuxConstants.CLONE_THREAD) != 0;
        var cloneSighand = (flags & LinuxConstants.CLONE_SIGHAND) != 0;
        var cloneVfork = (flags & LinuxConstants.CLONE_VFORK) != 0;

        if (cloneThread && !cloneVm) return -(int)Errno.EINVAL;
        if (cloneSighand && !cloneVm) return -(int)Errno.EINVAL;
        if (cloneThread && !cloneSighand) return -(int)Errno.EINVAL;
        if (cloneVfork && !cloneVm) return -(int)Errno.EINVAL;

        return 0;
    }

    private async ValueTask<int> SysFork(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // fork = clone(0, 0, NULL, NULL, NULL) - no flags, copy everything
        return await SysClone(engine, 0, 0, 0, 0, 0, 0);
    }

    private async ValueTask<int> SysVfork(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // vfork = clone(CLONE_VM | CLONE_VFORK, 0, NULL, NULL, NULL)
        return await SysClone(engine, LinuxConstants.CLONE_VM | LinuxConstants.CLONE_VFORK, 0, 0, 0, 0, 0);
    }

    private async ValueTask<int> SysWait4(Engine engine, uint pid, uint statusPtr, uint options, uint rusagePtr,
        uint a5, uint a6)
    {
        var pidVal = (int)pid;
        var optVal = (int)options;
        var hang = (optVal & 1) != 0; // WNOHANG
        var wantStopped = (optVal & 2) != 0; // WUNTRACED/WSTOPPED
        var wantContinued = (optVal & 8) != 0; // WCONTINUED
        var noReap = (optVal & 0x01000000) != 0; // WNOWAIT - report but don't reap

        if (engine.Owner is not FiberTask fiberTask) return -(int)Errno.ECHILD;

        var currentProc = fiberTask.Process;
        var task = (FiberTask)engine.Owner!;
        var kernel = task.CommonKernel;

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
                            if (!engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        // Reap only if WNOWAIT is not set
                        if (!noReap)
                        {
                            currentProc.Children.Remove(childPid);
                            childProc.State = ProcessState.Dead;
                            kernel.UnregisterProcess(childPid);
                            kernel.CleanupDeadProcess(childProc);
                        }

                        // Also remove from global table? Or let it be garbage collected if no other refs?
                        // If we remove from global table, PID can be reused properly (if allocator reuses).
                        // Current allocator is monotonic increment.
                        return childPid;
                    }

                    if (wantStopped && childProc.State == ProcessState.Stopped && childProc.HasWaitableStop)
                    {
                        if (statusPtr != 0)
                        {
                            var stBuf = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf,
                                EncodeWaitStoppedStatus(childProc.StopSignal));
                            if (!engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
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
                            if (!engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }

                        childProc.HasWaitableContinue = false;
                        return childPid;
                    }
                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;

            if (hang) return 0;

            await new ChildStateAwaitable(currentProc, fiberTask, pidVal, wantStopped, wantContinued);
            if (fiberTask.HasInterruptingPendingSignal())
                return -(int)Errno.ERESTARTSYS;
        }
    }

    private async ValueTask<int> SysWaitPid(Engine engine, uint pid, uint statusPtr, uint options, uint a4,
        uint a5, uint a6)
    {
        // waitpid(pid, status, options) = wait4(pid, status, options, NULL)
        return await SysWait4(engine, pid, statusPtr, options, 0, 0, 0);
    }

    private async ValueTask<int> SysWaitId(Engine engine, uint idtype, uint id, uint infop, uint options,
        uint rusagePtr, uint a6)
    {
        if (engine.Owner is not FiberTask fiberTask) return -(int)Errno.ECHILD;

        // Logic similar to SysWait4 but with idtype
        var currentProc = fiberTask.Process;
        var task = (FiberTask)engine.Owner!;
        var kernel = task.CommonKernel;

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

                            if (!WriteSigInfo(engine, infop, info)) return -(int)Errno.EFAULT;
                        }

                        if (!wnowait)
                        {
                            currentProc.Children.Remove(childPid);

                            childProc.State = ProcessState.Dead;
                            kernel.UnregisterProcess(childPid);
                            kernel.CleanupDeadProcess(childProc);
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
                            if (!WriteSigInfo(engine, infop, info)) return -(int)Errno.EFAULT;
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
                            if (!WriteSigInfo(engine, infop, info)) return -(int)Errno.EFAULT;
                        }

                        if (!wnowait) childProc.HasWaitableContinue = false;
                        return 0;
                    }
                }
            }

            if (!hasChildren) return -(int)Errno.ECHILD;
            if (wnohang) return 0;

            var targetPid = (IdType)idtype switch
            {
                IdType.P_ALL => -1,
                IdType.P_PID => (int)id,
                IdType.P_PGID => -(int)id,
                _ => -1
            };

            await new ChildStateAwaitable(currentProc, fiberTask, targetPid, wstopped, wcontinued);
            if (fiberTask.HasInterruptingPendingSignal())
                return -(int)Errno.ERESTARTSYS;
        }
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
            return new SigInfo
            {
                Signo = (int)Signal.SIGCHLD,
                Pid = childProc.TGID,
                Status = childProc.ExitStatus,
                Code = 1 // CLD_EXITED
            };

        return new SigInfo
        {
            Signo = (int)Signal.SIGCHLD,
            Pid = childProc.TGID,
            Status = childProc.TermSignal,
            Code = childProc.CoreDumped ? 3 : 2 // CLD_DUMPED / CLD_KILLED
        };
    }

    private static bool WriteSigInfo(Engine engine, uint addr, SigInfo info)
    {
        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.Signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.Errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.Code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.Pid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), info.Uid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.Status);
        return engine.CopyToUser(addr, buf);
    }

    internal static void FinalizeProcessExit(FiberTask task, int exitStatus, bool exitedBySignal, int termSignal,
        bool coreDumped)
    {
        if (task.Process.State is ProcessState.Zombie or ProcessState.Dead) return;

        var sm = task.Process.Syscalls;

        sm.Close();
        ProcFsManager.OnProcessExit(sm, task.Process.TGID);
        if (task.Process.Mem.GetSharedRefCount() == 1)
            sm.SysVShm.OnProcessExit(task.Process.TGID, sm.Mem, task.CPU, task.Process);
        MarkProcessExitAndReparent(task, exitStatus, exitedBySignal, termSignal, coreDumped);

        var ppid = task.Process.PPID;
        if (ppid > 0)
        {
            var parentTask = task.CommonKernel.GetTask(ppid);
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }
    }

    internal static void MarkProcessExitAndReparent(FiberTask task, int exitStatus, bool exitedBySignal, int termSignal,
        bool coreDumped)
    {
        if (task.Process.State is ProcessState.Zombie or ProcessState.Dead) return;
        task.Process.State = ProcessState.Zombie;
        task.Process.ExitStatus = exitStatus;
        task.Process.ExitedBySignal = exitedBySignal;
        task.Process.TermSignal = termSignal;
        task.Process.CoreDumped = coreDumped;
        task.Process.HasWaitableStop = false;
        task.Process.HasWaitableContinue = false;

        if (task.Process.TGID == task.CommonKernel.InitPid)
        {
            var terminated = task.CommonKernel.TerminateRemainingProcessesForInitExit(task.Process.TGID);
            if (terminated > 0)
                Logger.LogDebug("[Exit] Init PID {Pid} exited; signaled {Count} remaining processes for termination",
                    task.Process.TGID, terminated);
        }
        else
        {
            var reparented = task.CommonKernel.ReparentChildrenToInit(task.Process.TGID);
            if (reparented > 0)
                Logger.LogDebug("[Exit] Reparented {Count} orphaned children from PID {Pid} to init PID {InitPid}",
                    reparented, task.Process.TGID, task.CommonKernel.InitPid);
        }

        task.Process.StateChangeEvent.Set();
        task.CommonKernel.TryReleaseProcessMemory(task.Process, task.CPU);
        task.CommonKernel.DetachProcessTasks(task.Process.TGID);

        if (task.CommonKernel.TryAutoReapZombie(task.Process))
            Logger.LogDebug("[Exit] Auto-reaped zombie PID {Pid} by engine init reaper", task.Process.TGID);
    }

    private async ValueTask<int> SysExecve(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        Logger.LogDebug(
            "[SysExecve] engine==task.CPU? {Same}, engine.State=0x{EngState:x}, task.CPU.State=0x{CpuState:x}",
            ReferenceEquals(engine, task.CPU), engine.State, task.CPU.State);

        var filename = ReadString(a1);
        if (string.IsNullOrEmpty(filename)) return -(int)Errno.EFAULT;

        // Resolve path via VFS/Host
        var (loc, guestPath) = ResolvePath(filename);

        if (!loc.IsValid)
        {
            Logger.LogDebug("[SysExecve] Could not resolve '{Filename}' to a valid Dentry (Guest: {GuestPath})",
                filename, guestPath);
            return -(int)Errno.ENOENT;
        }

        var dentry = loc.Dentry!;
        var mount = loc.Mount!;

        // ── Shebang (#!) detection ───────────────────────────────────────────────
        // Linux binfmt_script: if the file starts with "#!", parse interpreter path
        // and re-execute with the interpreter as the program.
        var headerBuf = new byte[256];
        var headerLen = 0;
        if (dentry.Inode != null)
        {
            // Use Inode.Read() so this works for any filesystem, not just HostInode
            var tmpFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
            headerLen = dentry.Inode.Read(tmpFile, headerBuf.AsSpan(), 0);
            if (headerLen < 0) headerLen = 0;
        }

        if (headerLen >= 4 && headerBuf[0] == '#' && headerBuf[1] == '!')
        {
            // ...
            // Parse the shebang line: #!<interpreter> [optional-arg]\n
            var lineEnd = Array.IndexOf(headerBuf, (byte)'\n', 2);
            if (lineEnd < 0) lineEnd = headerLen;
            var shebangLine = Encoding.UTF8.GetString(headerBuf, 2, lineEnd - 2).Trim();

            string interpPath;
            string? interpArg = null;
            var spaceIdx = shebangLine.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                interpPath = shebangLine[..spaceIdx];
                interpArg = shebangLine[(spaceIdx + 1)..].Trim();
                if (string.IsNullOrEmpty(interpArg)) interpArg = null;
            }
            else
            {
                interpPath = shebangLine;
            }

            Logger.LogInformation(
                "[SysExecve] Shebang detected: interpreter='{Interp}', arg='{Arg}', script='{Script}'",
                interpPath, interpArg ?? "(none)", guestPath);

            // Resolve interpreter
            var (interpLoc, interpGuestPath) = ResolvePath(interpPath);
            if (!interpLoc.IsValid)
            {
                Logger.LogWarning("[SysExecve] Shebang interpreter '{Interp}' not found", interpPath);
                return -(int)Errno.ENOENT;
            }

            var interpDentry = interpLoc.Dentry!;
            var interpMount = interpLoc.Mount!;

            // Read original args (must be done BEFORE clearing memory)
            List<string> origArgs = [];
            if (a2 != 0)
            {
                var curr = a2;
                var ptrBuf = new byte[4];
                while (true)
                {
                    if (!engine.CopyFromUser(curr, ptrBuf)) break;
                    var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                    if (strPtr == 0) break;
                    origArgs.Add(ReadString(strPtr));
                    curr += 4;
                }
            }

            // Read original envs
            List<string> origEnvs = [];
            if (a3 != 0)
            {
                var curr = a3;
                var ptrBuf = new byte[4];
                while (true)
                {
                    if (!engine.CopyFromUser(curr, ptrBuf)) break;
                    var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                    if (strPtr == 0) break;
                    origEnvs.Add(ReadString(strPtr));
                    curr += 4;
                }
            }

            // Build new args: [interpreter, interpArg?, scriptPath, origArgs[1:]]
            List<string> newArgs = [interpPath];
            if (interpArg != null) newArgs.Add(interpArg);
            newArgs.Add(guestPath);
            if (origArgs.Count > 1)
                newArgs.AddRange(origArgs.Skip(1));

            // Close close-on-exec descriptors (FD_CLOEXEC is per-fd).
            var toCloseShebang = GetCloseOnExecFds();
            foreach (var fd in toCloseShebang) FreeFD(fd);

            // Reset signals
            var ignoredShebang = task.Process.SignalActions
                .Where(kv => kv.Value.Handler == 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            task.Process.SignalActions.Clear();
            foreach (var kv in ignoredShebang) task.Process.SignalActions[kv.Key] = kv.Value;
            task.PendingSignals = 0;
            task.AltStackSp = 0;
            task.AltStackSize = 0;
            task.AltStackFlags = 0;

            task.ExitRobustList();
            task.RobustListHead = 0;
            task.RobustListSize = 0;

            Logger.LogInformation("[SysExecve] Re-executing as: {Args}", string.Join(" ", newArgs));
            Logger.LogDebug("[SysExecve] Shebang re-exec argc={ArgCount} envc={EnvCount}",
                newArgs.Count, origEnvs.Count);
            foreach (var env in origEnvs.Where(e => e.StartsWith("APK_", StringComparison.Ordinal)))
                Logger.LogDebug("[SysExecve] Shebang env: {Env}", env);

            try
            {
                task.Process.Exec(interpLoc.Dentry!, interpGuestPath, [.. newArgs], [.. origEnvs], interpLoc.Mount!);
                CredentialService.ApplyExecSetIdOnExec(task.Process, interpLoc.Dentry!.Inode!);
                ProcFsManager.OnProcessExec(this, task.Process);
                task.SignalVforkDone(); // vfork: wake parent after exec
                return 0;
            }
            catch (Exception ex)
            {
                var rc = MapExecveExceptionToErrno(ex);
                Logger.LogWarning(ex, "[SysExecve] Shebang exec failed: rc={Rc} message={Message}", rc, ex.Message);
                return rc;
            }
        }

        // ── ELF magic validation ─────────────────────────────────────────────────
        // Validate BEFORE clearing memory. If the file isn't a valid ELF,
        // return -ENOEXEC without destroying the process's address space.
        if (headerLen < 4 || headerBuf[0] != 0x7F || headerBuf[1] != (byte)'E' ||
            headerBuf[2] != (byte)'L' || headerBuf[3] != (byte)'F')
        {
            Logger.LogWarning("[SysExecve] '{Path}' is not an ELF binary (magic: {M0:X2} {M1:X2} {M2:X2} {M3:X2})",
                guestPath,
                headerLen > 0 ? headerBuf[0] : 0, headerLen > 1 ? headerBuf[1] : 0,
                headerLen > 2 ? headerBuf[2] : 0, headerLen > 3 ? headerBuf[3] : 0);
            return -(int)Errno.ENOEXEC;
        }


        // Read Args (must be done BEFORE clearing memory)
        List<string> args = [];
        if (a2 != 0)
        {
            var curr = a2;
            var ptrBuf = new byte[4];
            while (true)
            {
                if (!engine.CopyFromUser(curr, ptrBuf)) break;
                var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                if (strPtr == 0) break;
                args.Add(ReadString(strPtr));
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
                if (!engine.CopyFromUser(curr, ptrBuf)) break;
                var strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                if (strPtr == 0) break;
                envs.Add(ReadString(strPtr));
                curr += 4;
            }
        }

        // Close close-on-exec descriptors (FD_CLOEXEC is per-fd).
        var toClose = GetCloseOnExecFds();
        foreach (var fd in toClose) FreeFD(fd);

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

        task.ExitRobustList();
        task.RobustListHead = 0;
        task.RobustListSize = 0;

        // Load new ELF via Process.Exec (Handles memory clearing + vDSO setup + ELF loading)
        Logger.LogInformation("[SysExecve] Loading {GuestPath} with {ArgCount} args, {EnvCount} envs via Process.Exec",
            guestPath, args.Count,
            envs.Count);
        foreach (var arg in args) Logger.LogDebug("  arg: {Arg}", arg);

        try
        {
            task.Process.Exec(dentry, guestPath, [.. args], [.. envs], mount);
            // No namespace/nosuid/no_new_privs yet: apply classic setuid/setgid exec semantics directly.
            CredentialService.ApplyExecSetIdOnExec(task.Process, dentry.Inode!);
            ProcFsManager.OnProcessExec(this, task.Process);
            task.SignalVforkDone(); // vfork: wake parent after exec
            return 0; // Success (Task continues at new EIP set by LoadExecutable)
        }
        catch (Exception ex) // Catch other exceptions during execve
        {
            var rc = MapExecveExceptionToErrno(ex);
            Logger.LogWarning(ex, "Execve failed: rc={Rc} message={Message}", rc, ex.Message);
            return rc;
        }
    }

    private static int MapExecveExceptionToErrno(Exception ex)
    {
        return ex switch
        {
            OutOfMemoryException => -(int)Errno.ENOMEM,
            FileNotFoundException => -(int)Errno.ENOENT,
            DirectoryNotFoundException => -(int)Errno.ENOENT,
            UnauthorizedAccessException => -(int)Errno.EACCES,
            _ => -(int)Errno.ENOENT
        };
    }

    private async ValueTask<int> SysSetSid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // setsid()
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
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

    private async ValueTask<int> SysSetPgid(Engine engine, uint pid, uint pgid, uint a3, uint a4, uint a5,
        uint a6)
    {
        // setpgid(pid, pgid)
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

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

    private async ValueTask<int> SysGetSid(Engine engine, uint pid, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = (FiberTask)engine.Owner!;
        var kernel = task.CommonKernel;

        var targetPid = (int)pid == 0 ? (engine.Owner as FiberTask)!.Process.TGID : (int)pid;
        var targetProc = kernel.GetProcess(targetPid);
        if (targetProc == null) return -(int)Errno.ESRCH;

        return targetProc.SID;
    }

    private async ValueTask<int> SysGetPgrp(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        return task.Process.PGID;
    }

    private async ValueTask<int> SysPrlimit64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pid = (int)a1;
        var resource = (int)a2;
        var newLimitPtr = a3;
        var oldLimitPtr = a4;

        if (pid != 0 && (GetTID == null || pid != GetTID(engine))) return -(int)Errno.EPERM;

        if (oldLimitPtr != 0)
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), 1024); // soft
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8), 4096); // hard
            if (!engine.CopyToUser(oldLimitPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private ValueTask<int> SysMagicDebug(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ValueTask.FromResult(-(int)Errno.EPERM);
    }
}
