using System.Buffers.Binary;
using Fiberish.Auth.Cred;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Loader;
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

    private ValueTask<int> SysClone(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        => CloneTask(engine, a1, a2, a3, a4, a5);

    private async ValueTask<int> CloneTask(Engine engine, uint flags, ulong stackPtr, ulong ptidPtr, ulong tls,
        ulong ctidPtr)
    {
        if (engine.Owner is not FiberTask current) return -(int)Errno.EPERM;

        var flagError = ValidateCloneFlags(current, flags);
        if (flagError != 0) return flagError;

        if (engine.IsAArch64 &&
            (stackPtr > engine.UserAddressLimit ||
             ((flags & LinuxConstants.CLONE_PARENT_SETTID) != 0 && !engine.IsUserRange(ptidPtr, 4)) ||
             ((flags & (LinuxConstants.CLONE_CHILD_SETTID | LinuxConstants.CLONE_CHILD_CLEARTID)) != 0 &&
              !engine.IsUserRange(ctidPtr, 4))))
            return -(int)Errno.EFAULT;

        FiberTask child;
        try
        {
            child = await current.Clone((int)flags, stackPtr, ptidPtr, tls, ctidPtr);
        }
        catch (OutOfMemoryException)
        {
            return -(int)Errno.ENOMEM;
        }
        catch (ArgumentException)
        {
            return -(int)Errno.EINVAL;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "SysClone InvalidOperationException: {Message}", ex.Message);
            return -(int)Errno.EFAULT;
        }

        if ((flags & LinuxConstants.CLONE_THREAD) == 0)
            ProcFsManager.OnProcessStart(child.Process.Syscalls, child.Process);

        return child.TID;
    }

    private static int ValidateCloneFlags(FiberTask current, uint flags)
    {
        var exitSignal = flags & 0xffu;
        var cloneVm = (flags & LinuxConstants.CLONE_VM) != 0;
        var cloneThread = (flags & LinuxConstants.CLONE_THREAD) != 0;
        var cloneSighand = (flags & LinuxConstants.CLONE_SIGHAND) != 0;
        var cloneVfork = (flags & LinuxConstants.CLONE_VFORK) != 0;
        var cloneParent = (flags & LinuxConstants.CLONE_PARENT) != 0;

        const uint namespaceFlags = LinuxConstants.CLONE_NEWNS |
                                    LinuxConstants.CLONE_NEWUTS |
                                    LinuxConstants.CLONE_NEWIPC |
                                    LinuxConstants.CLONE_NEWUSER |
                                    LinuxConstants.CLONE_NEWPID |
                                    LinuxConstants.CLONE_NEWNET |
                                    LinuxConstants.CLONE_NEWCGROUP;

        if (cloneThread && !cloneVm) return -(int)Errno.EINVAL;
        if (cloneSighand && !cloneVm) return -(int)Errno.EINVAL;
        if (cloneThread && !cloneSighand) return -(int)Errno.EINVAL;
        if (cloneVfork && !cloneVm) return -(int)Errno.EINVAL;
        if (cloneParent && current.Process.TGID == current.CommonKernel.InitPid)
            return -(int)Errno.EINVAL;

        if ((flags & namespaceFlags) != 0)
        {
            if (cloneThread || (cloneParent && (flags & (LinuxConstants.CLONE_NEWPID | LinuxConstants.CLONE_NEWUSER)) != 0))
                return -(int)Errno.EINVAL;
            if ((flags & ~LinuxConstants.CLONE_NEWUSER) != 0 &&
                !current.Process.HasEffectiveCapability(Process.CapabilitySysAdmin))
                return -(int)Errno.EPERM;
            return -(int)Errno.EINVAL;
        }

        var supportedFlags = LinuxConstants.CLONE_VM |
                             LinuxConstants.CLONE_FS |
                             LinuxConstants.CLONE_FILES |
                             LinuxConstants.CLONE_SIGHAND |
                             LinuxConstants.CLONE_PTRACE |
                             LinuxConstants.CLONE_VFORK |
                             LinuxConstants.CLONE_PARENT |
                             LinuxConstants.CLONE_THREAD |
                             LinuxConstants.CLONE_SYSVSEM |
                             LinuxConstants.CLONE_SETTLS |
                             LinuxConstants.CLONE_PARENT_SETTID |
                             LinuxConstants.CLONE_CHILD_CLEARTID |
                             LinuxConstants.CLONE_DETACHED |
                             LinuxConstants.CLONE_UNTRACED |
                             LinuxConstants.CLONE_CHILD_SETTID;

        if ((flags & ~(supportedFlags | exitSignal)) != 0)
            return -(int)Errno.EINVAL;

        // CLONE_PTRACE / CLONE_UNTRACED are ptrace-facing hints; ptrace itself is not modeled,
        // so we accept them as no-ops for compatibility. CLONE_DETACHED is obsolete and ignored
        // on Linux as well.

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
        // Linux wait4 valid options: WNOHANG(1) | WUNTRACED(2) | WCONTINUED(8) | __WNOTHREAD(0x20000000) | __WALL(0x40000000) | __WCLONE(0x80000000)
        // Linux accepts WNOHANG/WUNTRACED/WCONTINUED plus the __W* flags; keep our
        // existing WNOWAIT extension so callers relying on non-reaping waits still work.
        const uint ValidWait4Options = 0xE100000Bu;
        if ((options & ~ValidWait4Options) != 0)
            return -(int)Errno.EINVAL;

        var pidVal = (int)pid;
        if (pidVal == int.MinValue)
            return -(int)Errno.ESRCH;

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

                        if (rusagePtr != 0 && !WriteRusage(engine, rusagePtr, childProc.GetSelfCpuTimeSnapshot()))
                            return -(int)Errno.EFAULT;

                        // Reap only if WNOWAIT is not set
                        if (!noReap)
                        {
                            currentProc.AccumulateChildrenCpuTime(childProc.GetReapedCpuTimeSnapshot());
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

        const uint ValidWaitIdOptions = 0x0100000Fu; // WNOHANG(1) | WSTOPPED(2) | WEXITED(4) | WCONTINUED(8) | WNOWAIT(0x01000000)
        if ((options & ~ValidWaitIdOptions) != 0)
            return -(int)Errno.EINVAL;

        var wnohang = (options & 1) != 0;
        var wstopped = (options & 2) != 0;
        var wexited = (options & 4) != 0;
        var wcontinued = (options & 8) != 0;
        var wnowait = (options & 0x01000000) != 0;
        if (!wexited && !wstopped && !wcontinued)
            return -(int)Errno.EINVAL;

        var idType = (IdType)idtype;
        if (idType is not (IdType.P_ALL or IdType.P_PID or IdType.P_PGID))
            return -(int)Errno.EINVAL;
        switch (idType)
        {
            // P_ALL ignores id (waitid04 passes a child pid with P_ALL).
            case IdType.P_PID when (int)id <= 0:
                return -(int)Errno.EINVAL;
            case IdType.P_PGID when (int)id < 0:
                return -(int)Errno.EINVAL;
        }

        // Logic similar to SysWait4 but with idtype
        var currentProc = fiberTask.Process;
        var task = (FiberTask)engine.Owner!;
        var kernel = task.CommonKernel;
        // P_PGID with id 0 targets the caller's process group, not pid/pgid 0.
        var waitPgid = (int)id == 0 ? currentProc.PGID : (int)id;

        while (true)
        {
            var hasChildren = false;

            foreach (var childPid in currentProc.Children)
            {
                var childProc = kernel.GetProcess(childPid);
                if (childProc == null) continue;

                var match = false;
                if (idType == IdType.P_ALL) match = true;
                else if (idType == IdType.P_PID && childPid == (int)id) match = true;
                else if (idType == IdType.P_PGID && childProc.PGID == waitPgid) match = true;

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

                        if (rusagePtr != 0 && !WriteRusage(engine, rusagePtr, childProc.GetSelfCpuTimeSnapshot()))
                            return -(int)Errno.EFAULT;

                        if (!wnowait)
                        {
                            currentProc.AccumulateChildrenCpuTime(childProc.GetReapedCpuTimeSnapshot());
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
            if (wnohang)
            {
                if (infop != 0 && !WriteSigInfo(engine, infop, default))
                    return -(int)Errno.EFAULT;
                return 0;
            }

            var targetPid = idType switch
            {
                IdType.P_ALL => -1,
                IdType.P_PID => (int)id,
                IdType.P_PGID => -waitPgid,
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

    internal static bool ShouldNotifyParentOfChildStateChange(Process? parentProc)
    {
        if (parentProc == null)
            return true;

        if (!parentProc.SignalActions.TryGetValue((int)Signal.SIGCHLD, out var action))
            return true;

        return (action.Flags & LinuxConstants.SA_NOCLDSTOP) == 0;
    }

    private static bool ShouldAutoReapExitedChild(Process? parentProc)
    {
        if (parentProc == null)
            return false;

        if (!parentProc.SignalActions.TryGetValue((int)Signal.SIGCHLD, out var action))
            return false;

        return action.Handler == 1 || (action.Flags & LinuxConstants.SA_NOCLDWAIT) != 0;
    }

    private static bool ShouldDeliverSigChldOnExit(Process? parentProc)
    {
        if (parentProc == null)
            return false;

        if (!parentProc.SignalActions.TryGetValue((int)Signal.SIGCHLD, out var action))
            return true;

        return action.Handler != 1;
    }

    private static bool WriteSigInfo(Engine engine, uint addr, SigInfo info)
    {
        if (engine.IsAArch64)
        {
            Span<byte> arm64Info = stackalloc byte[128];
            FiberTask.WriteAArch64SigInfo(arm64Info, info);
            return engine.CopyToUser(addr, arm64Info);
        }

        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.Signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.Errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.Code);

        // SIGPOLL/SIGIO carry si_band (long, 4 bytes on i386) and si_fd.
        if (info.HasFdPayload)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)info.Band);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), info.Fd);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.Pid);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), info.Uid);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.Status);
        }
        return engine.CopyToUser(addr, buf);
    }

    internal static void FinalizeProcessExit(FiberTask task, int exitStatus, bool exitedBySignal, int termSignal,
        bool coreDumped)
    {
        if (task.Process.State is ProcessState.Zombie or ProcessState.Dead) return;

        var sm = task.Process.Syscalls;

        sm.ReleasePosixLocksForProcess(task.Process);
        sm.Close();
        ProcFsManager.OnProcessExit(sm, task.Process.TGID);
        sm.SysVSem.OnProcessExit(task.Process);
        if (task.Process.Mem.GetSharedRefCount() == 1)
            sm.SysVShm.OnProcessExit(task.Process.TGID, sm.Mem, task.CPU, task.Process);
        MarkProcessExitAndReparent(task, exitStatus, exitedBySignal, termSignal, coreDumped);

        var ppid = task.Process.PPID;
        if (ppid > 0)
        {
            var parentProc = task.CommonKernel.GetProcess(ppid);
            if (ShouldDeliverSigChldOnExit(parentProc))
                task.CommonKernel.SignalProcessInfo(ppid, (int)Signal.SIGCHLD, BuildSigchldInfoForExit(task.Process));

            if (ShouldAutoReapExitedChild(parentProc))
            {
                parentProc!.AccumulateChildrenCpuTime(task.Process.GetReapedCpuTimeSnapshot());
                _ = parentProc!.Children.Remove(task.Process.TGID);
                task.Process.State = ProcessState.Dead;
                task.CommonKernel.UnregisterProcess(task.Process.TGID);
                task.CommonKernel.CleanupDeadProcess(task.Process);
            }
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
        task.Process.FreezeCpuTimeSnapshot();

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

    private async ValueTask<int> SysExecve(Engine engine, ulong a1, ulong a2, ulong a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug(
                "[SysExecve] engine==task.CPU? {Same}, engine.State=0x{EngState:x}, task.CPU.State=0x{CpuState:x}",
                ReferenceEquals(engine, task.CPU), engine.State, task.CPU.State);

        var pathErr = ReadPathArgumentBytes(a1, out var filename);
        if (pathErr != 0) return pathErr;
        using var _ = filename;

        // Linux copies argv/envp before it opens the executable, so EFAULT/E2BIG take
        // precedence over ENOENT/EACCES/ETXTBSY. Keep the same ordering here.
        var argumentBudget = GetExecArgMaxBytes() - filename.Length - 1;
        var (args, argsError) = ReadUserCStringVectorBytes(a2, ref argumentBudget);
        if (argsError < 0) return argsError;
        var (envs, envsError) = ReadUserCStringVectorBytes(a3, ref argumentBudget);
        if (envsError < 0) return envsError;

        var lookup = PathWalker.PathWalkWithData(filename.UnsafeBuffer, filename.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError)
        {
            Logger.LogDebug("[SysExecve] Could not resolve '{Filename}' (rc={Rc})",
                FsEncoding.DecodeUtf8Lossy(filename.Span), lookup.ErrorCode);
            return lookup.ErrorCode;
        }

        var loc = lookup.Path;
        var guestPathRaw = filename.Span.ToArray();
        if (!loc.IsValid)
            return -(int)Errno.ENOENT;

        var execFnPathRaw = guestPathRaw;
        var imageInstalled = false;
        try
        {
            // The syscall's initial shebang check can be followed by four
            // interpreter probes, including the final ELF probe.
            for (var depth = 0; depth <= 4; depth++)
            {
                var dentry = loc.Dentry!;
                var mount = loc.Mount!;
                var access = task.Process.CheckExecutableAccess(dentry, mount);
                if (access < 0) return access;

                var (file, openedStream) = ElfLoader.OpenForExec(dentry, mount, task);
                using var stream = openedStream;
                var header = new byte[256];
                var length = await stream.ReadForTaskAsync(task, header);
                if (task.IsRetiring || task.Exited) return -(int)Errno.EINTR;

                if (ShebangParser.TryParse(header.AsSpan(0, length), out var interpreter, out var option))
                {
                    loc = WithExecEngine(engine, () => ResolvePathBytes(interpreter).loc);
                    if (!loc.IsValid) return -(int)Errno.ENOENT;
                    args = Process.RewriteShebangArguments(interpreter, option, guestPathRaw, args);
                    guestPathRaw = interpreter;
                    continue;
                }

                var main = await ElfLoader.ReadImageAsync(file, stream, task);
                if (task.IsRetiring || task.Exited) return -(int)Errno.EINTR;
                var interpLoc = main.InterpPathRaw is { } path
                    ? WithExecEngine(engine, () => ElfLoader.ResolveInterpreter(path, this, task.Process))
                    : PathLocation.None;
                var (interpFile, openedInterpStream) = interpLoc.IsValid
                    ? ElfLoader.OpenForExec(interpLoc.Dentry!, interpLoc.Mount!, task)
                    : default;
                using var interpStream = openedInterpStream;
                ElfLoader.PreparedImage? interpImage = null;
                if (interpStream != null)
                {
                    try
                    {
                        interpImage = await ElfLoader.ReadImageAsync(interpFile!, interpStream, task, main.Elf.Architecture);
                    }
                    catch (Exception ex) when (ElfLoader.IsInvalidElfFormat(ex))
                    {
                        throw new ExecFileException(Errno.ELIBBAD, "Invalid ELF interpreter format.", ex);
                    }
                }

                if (task.IsRetiring || task.Exited) return -(int)Errno.EINTR;

                // Recheck write access after waiting. No guest can run between
                // this check and acquiring the executable mapping holds.
                access = task.Process.CheckExecutableAccess(file.OpenedInode, mount);
                if (access < 0) return access;
                if (interpFile != null)
                {
                    access = task.Process.CheckExecutableAccess(interpFile.OpenedInode, interpLoc.Mount!);
                    if (access < 0) return access;
                }

                var identity = task.Process.Exec(dentry, guestPathRaw, args, envs, mount, execFnPathRaw, engine,
                    main, interpImage);
                imageInstalled = true;
                ApplyExecSuccessState(task);
                task.Process.HasExeced = true;
                CredentialService.ApplyExecIdentity(task.Process, identity);
                ProcFsManager.OnProcessExec(this, task.Process);
                task.SignalVforkDone();
                return 0;
            }

            return -(int)Errno.ENOEXEC;
        }
        catch (Exception ex)
        {
            if (imageInstalled || ex is FatalExecException)
            {
                Logger.LogError(ex, "Fatal exec failure");
                task.TerminateFailedExec();
                return 0;
            }

            var rc = MapExecveExceptionToErrno(ex);
            Logger.LogDebug(ex, "Execve failed: rc={Rc} path={Path}", rc, FsEncoding.DecodeUtf8Lossy(guestPathRaw));
            return rc;
        }
    }

    private T WithExecEngine<T>(Engine engine, Func<T> action)
    {
        var previous = CurrentSyscallEngine;
        CurrentSyscallEngine = engine;
        try
        {
            return action();
        }
        finally
        {
            CurrentSyscallEngine = previous;
        }
    }

    private static void ApplyExecSuccessState(FiberTask task)
    {
        // The descriptor table was unshared and the robust list processed in the exec
        // commit phase. Remaining side effects are applied after the new image is live.
        // Linux applies these only after the new image has been installed. On a
        // failed execve the descriptor table, signal dispositions and altstack must
        // remain exactly as they were.
        task.Process.Syscalls.CloseExecDescriptors();

        task.Process.ResetSignalDispositionsForExec();
        task.ResetAltStack();
        task.ChildClearTidPtr = 0;
        task.RobustListHead = 0;
        task.RobustListSize = 0;
    }

    private static int MapExecveExceptionToErrno(Exception ex)
    {
        return ex switch
        {
            ExecFileException exec => -(int)exec.Error,
            OutOfMemoryException => -(int)Errno.ENOMEM,
            TextFileBusyException => -(int)Errno.ETXTBSY,
            EndOfStreamException or InvalidDataException or NotSupportedException or InvalidOperationException =>
                -(int)Errno.ENOEXEC,
            OverflowException => -(int)Errno.E2BIG,
            FileNotFoundException => -(int)Errno.ENOENT,
            DirectoryNotFoundException => -(int)Errno.ENOENT,
            UnauthorizedAccessException => -(int)Errno.EACCES,
            _ => -(int)Errno.ENOEXEC
        };
    }

    private async ValueTask<int> SysSetSid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // setsid()
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var proc = task.Process;

        // If caller is a process group leader, or any live process has PGID == caller's PID, return EPERM.
        if (proc.PGID == proc.TGID) return -(int)Errno.EPERM;
        foreach (var other in task.CommonKernel.GetProcessesSnapshot())
        {
            if (other.State != ProcessState.Dead && other.PGID == proc.TGID)
                return -(int)Errno.EPERM;
        }

        proc.SID = proc.TGID;
        proc.PGID = proc.TGID;

        // A new session has no controlling terminal. The tty keeps its old session
        // association for the processes that stayed behind.
        proc.ControllingTty = null;

        return proc.SID;
    }

    private async ValueTask<int> SysSetPgid(Engine engine, uint pid, uint pgid, uint a3, uint a4, uint a5,
        uint a6)
    {
        // setpgid(pid, pgid)
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if ((int)pgid < 0) return -(int)Errno.EINVAL;

        var kernel = task.CommonKernel;
        var targetPid = (int)pid == 0 ? task.Process.TGID : (int)pid;
        var targetPgid = (int)pgid == 0 ? targetPid : (int)pgid;

        var targetProc = kernel.GetProcess(targetPid);
        if (targetProc == null) return -(int)Errno.ESRCH;

        // Target must be calling process or child of calling process; otherwise ESRCH on Linux.
        if (targetProc.TGID != task.Process.TGID && targetProc.PPID != task.Process.TGID)
            return -(int)Errno.ESRCH;

        // Cannot change child's PGID after it has exec'd.
        if (targetProc.TGID != task.Process.TGID && targetProc.HasExeced)
            return -(int)Errno.EACCES;

        // Target must be in same session.
        if (targetProc.SID != task.Process.SID) return -(int)Errno.EPERM;

        // Cannot change if session leader.
        if (targetProc.SID == targetProc.TGID) return -(int)Errno.EPERM;

        // When moving into a different PGID, a process group with targetPgid must already exist in the same session.
        if (targetPgid != targetProc.TGID)
        {
            var foundGroup = false;
            foreach (var p in kernel.GetProcessesSnapshot())
            {
                if (p.State != ProcessState.Dead && p.PGID == targetPgid && p.SID == task.Process.SID)
                {
                    foundGroup = true;
                    break;
                }
            }

            if (!foundGroup)
                return -(int)Errno.EPERM;
        }

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

    private async ValueTask<int> SysSetRlimit(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var read = engine.IsAArch64
            ? TryReadRlimit64(engine, a2, out var newLimit)
            : TryReadRlimit32(engine, a2, out newLimit);
        if (!read) return -(int)Errno.EFAULT;
        return ApplyResourceLimit(task.Process, (int)a1, newLimit);
    }

    private async ValueTask<int> SysGetRlimit(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (!task.Process.TryGetResourceLimit((int)a1, out var limit)) return -(int)Errno.EINVAL;
        return WriteRlimit32Signed(engine, a2, limit) ? 0 : -(int)Errno.EFAULT;
    }

    private async ValueTask<int> SysUGetRlimit(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (!task.Process.TryGetResourceLimit((int)a1, out var limit)) return -(int)Errno.EINVAL;
        var written = engine.IsAArch64 ? WriteRlimit64(engine, a2, limit) : WriteRlimit32(engine, a2, limit);
        return written ? 0 : -(int)Errno.EFAULT;
    }

    private async ValueTask<int> SysPrlimit64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var targetProcess = ResolvePrlimitTarget(task, (int)a1, out var targetError);
        if (targetError != 0) return targetError;

        var resource = (int)a2;
        var newLimitPtr = a3;
        var oldLimitPtr = a4;

        if (!targetProcess!.TryGetResourceLimit(resource, out var currentLimit)) return -(int)Errno.EINVAL;
        if (oldLimitPtr != 0 && !WriteRlimit64(engine, oldLimitPtr, currentLimit)) return -(int)Errno.EFAULT;

        if (newLimitPtr == 0) return 0;
        if (!TryReadRlimit64(engine, newLimitPtr, out var newLimit)) return -(int)Errno.EFAULT;

        return ApplyResourceLimit(targetProcess, resource, newLimit);
    }

    private ValueTask<int> SysMagicDebug(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ValueTask.FromResult(-(int)Errno.EPERM);
    }

    private static int ApplyResourceLimit(Process process, int resource, ResourceLimit newLimit)
    {
        const ulong SysctlNrOpen = 1048576;
        if (!process.TryGetResourceLimit(resource, out var currentLimit)) return -(int)Errno.EINVAL;
        if (newLimit.Soft > newLimit.Hard) return -(int)Errno.EINVAL;

        if (resource == LinuxConstants.RLIMIT_NOFILE &&
            (newLimit.Soft > SysctlNrOpen || newLimit.Hard > SysctlNrOpen))
            return -(int)Errno.EPERM;

        // CAP_SYS_RESOURCE is not modeled; root can still raise hard limits.
        if (process.EUID != 0 && newLimit.Hard > currentLimit.Hard) return -(int)Errno.EPERM;

        return process.TrySetResourceLimit(resource, newLimit) ? 0 : -(int)Errno.EINVAL;
    }

    private static Process? ResolvePrlimitTarget(FiberTask task, int pid, out int error)
    {
        error = 0;
        if (pid == 0 || pid == task.Process.TGID || pid == task.TID) return task.Process;

        var target = task.CommonKernel.GetProcess(pid);
        if (target == null)
        {
            error = -(int)Errno.ESRCH;
            return null;
        }

        error = -(int)Errno.EPERM;
        return null;
    }

    private static bool TryReadRlimit32(Engine engine, uint ptr, out ResourceLimit limit)
    {
        var buf = new byte[8];
        if (!engine.CopyFromUser(ptr, buf))
        {
            limit = default;
            return false;
        }

        limit = new ResourceLimit(ExpandLegacyRlimit(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4))),
            ExpandLegacyRlimit(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4))));
        return true;
    }

    private static bool TryReadRlimit64(Engine engine, uint ptr, out ResourceLimit limit)
    {
        var buf = new byte[16];
        if (!engine.CopyFromUser(ptr, buf))
        {
            limit = default;
            return false;
        }

        limit = new ResourceLimit(ExpandRlimit64(BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0, 8))),
            ExpandRlimit64(BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8))));
        return true;
    }

    private static bool WriteRlimit32Signed(Engine engine, uint ptr, ResourceLimit limit)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), NarrowSignedLegacyRlimit(limit.Soft));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), NarrowSignedLegacyRlimit(limit.Hard));
        return engine.CopyToUser(ptr, buf);
    }

    private static bool WriteRlimit32(Engine engine, uint ptr, ResourceLimit limit)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), NarrowLegacyRlimit(limit.Soft));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), NarrowLegacyRlimit(limit.Hard));
        return engine.CopyToUser(ptr, buf);
    }

    private static bool WriteRlimit64(Engine engine, uint ptr, ResourceLimit limit)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), NarrowRlimit64(limit.Soft));
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8), NarrowRlimit64(limit.Hard));
        return engine.CopyToUser(ptr, buf);
    }

    private static ulong ExpandLegacyRlimit(uint value)
    {
        return value == LinuxConstants.RLIM32_INFINITY ? LinuxConstants.RLIM64_INFINITY : value;
    }

    private static ulong ExpandRlimit64(ulong value)
    {
        return value == LinuxConstants.RLIM64_INFINITY ? LinuxConstants.RLIM64_INFINITY : value;
    }

    private static uint NarrowSignedLegacyRlimit(ulong value)
    {
        return value == LinuxConstants.RLIM64_INFINITY || value >= LinuxConstants.RLIM32_SIGNED_INFINITY
            ? LinuxConstants.RLIM32_SIGNED_INFINITY
            : (uint)value;
    }

    private static uint NarrowLegacyRlimit(ulong value)
    {
        return value == LinuxConstants.RLIM64_INFINITY || value >= LinuxConstants.RLIM32_INFINITY
            ? LinuxConstants.RLIM32_INFINITY
            : (uint)value;
    }

    private static ulong NarrowRlimit64(ulong value)
    {
        return value == LinuxConstants.RLIM64_INFINITY ? LinuxConstants.RLIM64_INFINITY : value;
    }
}
