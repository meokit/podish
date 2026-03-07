using Fiberish.Core.VFS.TTY;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Core;

public static class ProcessFactory
{
    public static Process CreateEngineInitProcess(KernelRuntime runtime, KernelScheduler scheduler, UTSNamespace? uts = null)
    {
        var initPid = scheduler.AllocateTaskId();
        var proc = new Process(initPid, runtime.Memory, runtime.Syscalls, uts)
        {
            PGID = initPid,
            SID = initPid,
            Name = "podish-init"
        };

        scheduler.RegisterProcess(proc);
        scheduler.SetInitPid(proc.TGID);
        ProcFsManager.OnProcessStart(runtime.Syscalls, proc);
        return proc;
    }

    public static FiberTask CreateInitProcess(KernelRuntime runtime, Dentry dentry, string guestPath, string[] args,
        string[] envs,
        KernelScheduler scheduler, TtyDiscipline? tty = null, Mount? mount = null, UTSNamespace? uts = null,
        int parentPid = 0)
    {
        var initPid = scheduler.AllocateTaskId();
        var proc = new Process(initPid, runtime.Memory, runtime.Syscalls, uts)
        {
            PGID = 0,
            SID = 0,
            PPID = parentPid
        };

        proc.PGID = proc.TGID;
        proc.SID = proc.TGID;

        // Set up controlling terminal for init process
        // This mimics what happens when a session leader calls TIOCSCTTY
        if (tty != null)
        {
            tty.SessionId = proc.SID;
            tty.ForegroundPgrp = proc.PGID;
            proc.ControllingTty = tty;
        }

        scheduler.RegisterProcess(proc);
        scheduler.SetInitPid(proc.TGID);

        var mainTask = new FiberTask(proc.TGID, proc, runtime.Engine, scheduler);
        runtime.Engine.Owner = mainTask;

        proc.LoadExecutable(dentry, guestPath, args, envs, mount ?? runtime.Syscalls.RootMount!);
        ProcFsManager.OnProcessStart(runtime.Syscalls, proc);

        if (parentPid > 0)
        {
            var parent = scheduler.GetProcess(parentPid);
            if (parent != null)
            {
                lock (parent.Children)
                {
                    if (!parent.Children.Contains(proc.TGID)) parent.Children.Add(proc.TGID);
                }
            }
        }

        return mainTask;
    }
}
