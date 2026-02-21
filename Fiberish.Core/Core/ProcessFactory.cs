using Fiberish.Core.VFS.TTY;
using Fiberish.Syscalls;

namespace Fiberish.Core;

public static class ProcessFactory
{
    public static FiberTask CreateInitProcess(KernelRuntime runtime, string exePath, string[] args, string[] envs,
        KernelScheduler scheduler, TtyDiscipline? tty = null)
    {
        var proc = new Process(FiberTask.NextTID(), runtime.Memory, runtime.Syscalls)
        {
            PGID = 0,
            SID = 0
        };

        proc.PGID = proc.TGID;
        proc.SID = proc.TGID;

        if (tty != null)
        {
            tty.SessionId = proc.SID;
            tty.ForegroundPgrp = proc.PGID;
        }

        scheduler.RegisterProcess(proc);

        var mainTask = new FiberTask(proc.TGID, proc, runtime.Engine, scheduler);
        runtime.Engine.Owner = mainTask;

        proc.LoadExecutable(exePath, args, envs);
        ProcFsManager.OnProcessStart(runtime.Syscalls, proc);

        return mainTask;
    }
}
