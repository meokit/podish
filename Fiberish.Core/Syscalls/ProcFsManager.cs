using System.Text;
using Fiberish.Core;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public static class ProcFsManager
{
    public static void OnProcessStart(SyscallManager sm, Process process)
    {
        try
        {
            var procDentry = sm.Root.Inode!.Lookup("proc");
            if (procDentry == null) return;

            // Should be a mount point
            if (procDentry.IsMounted && procDentry.MountRoot != null)
                procDentry = procDentry.MountRoot;

            if (procDentry.Inode == null) return;

            var pidStr = process.TGID.ToString();
            var pidDentry = procDentry.Inode.Lookup(pidStr);
            if (pidDentry == null)
            {
                pidDentry = new Dentry(pidStr, null, procDentry, procDentry.SuperBlock);
                procDentry.Inode.Mkdir(pidDentry, 0x1ED, 0, 0); // 555
            }

            // Create status file
            CreateOrUpdateProcFile(pidDentry, "status", () => GenerateStatus(process));
            CreateOrUpdateProcFile(pidDentry, "cmdline", () => GenerateCmdline(process));
            CreateOrUpdateProcFile(pidDentry, "stat", () => GenerateStat(process));
        }
        catch
        {
        }
    }

    public static void OnProcessExec(SyscallManager sm, Process process)
    {
        OnProcessStart(sm, process);
    }

    public static void OnProcessExit(SyscallManager sm, int pid)
    {
        try
        {
            var procDentry = sm.Root.Inode!.Lookup("proc");
            if (procDentry == null) return;

            if (procDentry.IsMounted && procDentry.MountRoot != null)
                procDentry = procDentry.MountRoot;

            if (procDentry.Inode == null) return;

            // Rmdir recursively? Tmpfs.Rmdir assumes empty.
            // We need to unlink children first.
            var pidStr = pid.ToString();
            var pidDentry = procDentry.Inode.Lookup(pidStr);
            if (pidDentry != null && pidDentry.Inode != null)
            {
                // Unlink children manually as Tmpfs doesn't support recursive delete
                // Or we extend Tmpfs. For now, manual.
                foreach (var child in pidDentry.Inode.GetEntries())
                {
                    if (child.Name == "." || child.Name == "..") continue;
                    pidDentry.Inode.Unlink(child.Name);
                }

                procDentry.Inode.Rmdir(pidStr);
            }
        }
        catch
        {
        }
    }

    private static void CreateOrUpdateProcFile(Dentry parent, string name, Func<string> contentGen)
    {
        var dentry = parent.Inode!.Lookup(name);
        if (dentry == null)
        {
            dentry = new Dentry(name, null, parent, parent.SuperBlock);
            parent.Inode.Create(dentry, 0x124, 0, 0); // 444
        }

        dentry.Inode!.Truncate(0);

        // Write content?
        // Since Tmpfs is static, we write it once at start.
        // For dynamic content, we'd need a specialized ProcInode.
        // For busybox simple ps, static snapshot at fork might be enough?
        // "ps" shows running processes. If status changes, it won't reflect.
        // But pid/ppid/name are mostly static.
        // Let's write initial content.

        var content = contentGen();
        var bytes = Encoding.UTF8.GetBytes(content);
        var f = new VFS.LinuxFile(dentry, FileFlags.O_WRONLY);
        try
        {
            dentry.Inode!.Write(f, bytes, 0);
        }
        finally
        {
            dentry.Inode!.Release(f);
        }
    }

    private static string GenerateStatus(Process process)
    {
        // Minimal status
        var stateChar = process.State switch
        {
            ProcessState.Running => 'R',
            ProcessState.Sleeping => 'S',
            ProcessState.Stopped => 'T',
            ProcessState.Zombie => 'Z',
            ProcessState.Dead => 'X',
            _ => 'R'
        };
        var stateMsg = stateChar switch
        {
            'R' => "R (running)",
            'S' => "S (sleeping)",
            'T' => "T (stopped)",
            'Z' => "Z (zombie)",
            'X' => "X (dead)",
            _ => "R (running)"
        };
        return $"Name:\t{process.Comm}\nState:\t{stateMsg}\nPid:\t{process.TGID}\nPPid:\t{process.PPID}\n";
    }

    private static string GenerateCmdline(Process process)
    {
        return Encoding.UTF8.GetString(process.CommandLineRaw);
    }

    private static string GenerateStat(Process process)
    {
        // Minimal stat for ps
        // pid (comm) state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime priority nice num_threads itrealvalue starttime vsize rss rsslim startcode endcode startstack kstkesp kstkeip signal blocked sigignore sigcatch wchan nswap cnswap exit_signal processor rt_priority policy
        
        var stateChar = process.State switch
        {
            ProcessState.Running => 'R',
            ProcessState.Sleeping => 'S',
            ProcessState.Stopped => 'T',
            ProcessState.Zombie => 'Z',
            ProcessState.Dead => 'X',
            _ => 'R'
        };

        // Determine TTY info
        int ttyNr = 0;
        int tpgid = -1;
        if (process.ControllingTty != null)
        {
            // Try to assign a device number. Standard /dev/ttyx is typically major 4. Let's just mock one or use 0x8800 for pty
            // Realistically busybox ps checks if ttyNr > 0 to display it.
            // Let's use 34816 (0x8800) which is commonly used for /dev/pts/0 or similar.
            ttyNr = 34816; 
            tpgid = process.ControllingTty.ForegroundPgrp;
        }

        return
            $"{process.TGID} ({process.Comm}) {stateChar} {process.PPID} {process.PGID} {process.SID} {ttyNr} {tpgid} 0 0 0 0 0 0 0 0 0 0 0 {process.Threads.Count} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
    }

    public static void Init(SyscallManager sm)
    {
        var procDentry = sm.Root.Inode!.Lookup("proc");
        if (procDentry == null) return;

        if (procDentry.IsMounted && procDentry.MountRoot != null)
            procDentry = procDentry.MountRoot;

        if (procDentry.Inode == null) return;

        CreateOrUpdateProcFile(procDentry, "mounts", () => GenerateMounts(sm));
        CreateOrUpdateProcFile(procDentry, "cpuinfo", GenerateCpuInfo);
        CreateOrUpdateProcFile(procDentry, "meminfo", GenerateMemInfo);
        CreateOrUpdateProcFile(procDentry, "version",
            () =>
                "Linux version 6.1.0-x86emu (jiangyiheng@antigravity) (gcc version 12.2.0 (Debian 12.2.0-14)) #1 SMP PREEMPT\n");
    }

    private static string GenerateMounts(SyscallManager sm)
    {
        var sb = new StringBuilder();
        foreach (var m in sm.MountList) sb.Append($"{m.Source} {m.Target} {m.FsType} {m.Options} 0 0\n");
        return sb.ToString();
    }

    private static string GenerateCpuInfo()
    {
        return
            "processor\t: 0\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t: 1\nmodel name\t: x86emu\nstepping\t: 1\nmicrocode\t: 0x1\ncpu MHz\t\t: 1000.000\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 1\ncore id\t\t: 0\ncpu cores\t: 1\napicid\t\t: 0\ninitial apicid\t: 0\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 1\nwp\t\t: yes\nflags\t\t: fpu tsc cx8 cmov mmx fxsr sse sse2\n\n";
    }

    private static string GenerateMemInfo()
    {
        return
            "MemTotal:        262144 kB\nMemFree:         131072 kB\nBuffers:              0 kB\nCached:               0 kB\nSwapCached:           0 kB\nActive:               0 kB\nInactive:             0 kB\nActive(anon):         0 kB\nInactive(anon):       0 kB\nActive(file):         0 kB\nInactive(file):       0 kB\nUnevictable:          0 kB\nMlocked:              0 kB\nSwapTotal:            0 kB\nSwapFree:             0 kB\nDirty:                0 kB\nWriteback:            0 kB\nAnonPages:            0 kB\nMapped:               0 kB\nShmem:                0 kB\nSlab:                 0 kB\nSReclaimable:         0 kB\nSUnreclaim:           0 kB\nKernelStack:          0 kB\nPageTables:           0 kB\nNFS_Unstable:         0 kB\nBounce:               0 kB\nWritebackTmp:         0 kB\nCommitLimit:     131072 kB\nCommitted_AS:         0 kB\nVmallocTotal:   34359738367 kB\nVmallocUsed:          0 kB\nVmallocChunk:   34359738367 kB\nHardwareCorrupted:     0 kB\nAnonHugePages:        0 kB\nHugePages_Total:       0\nHugePages_Free:        0\nHugePages_Rsvd:        0\nHugePages_Surp:        0\nHugepagesize:       2048 kB\nDirectMap4k:       4096 kB\nDirectMap2M:     258048 kB\n";
    }
}
