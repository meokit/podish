using System.Text;
using Fiberish.Core;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public static class ProcFsManager
{
    public static void OnProcessStart(SyscallManager sm, Process process)
    {
        // Dynamic procfs does not need eager inode creation.
        // Keep hook for compatibility with call sites.
    }

    public static void OnProcessExec(SyscallManager sm, Process process)
    {
        // Dynamic procfs content is generated during read/open.
    }

    public static void OnProcessExit(SyscallManager sm, int pid)
    {
        try
        {
            var loc = sm.PathWalk("/proc");
            if (!loc.IsValid) return;

            var pidStr = pid.ToString();
            loc.Dentry!.Children.Remove(pidStr);
        }
        catch
        {
        }
    }

    public static string GenerateStatus(Process process)
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

    public static string GenerateCmdline(Process process)
    {
        return Encoding.UTF8.GetString(process.CommandLineRaw);
    }

    public static string GenerateStat(Process process)
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

    public static string GenerateMounts(SyscallManager? sm)
    {
        if (sm == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var (source, target, fsType, options) in sm.MountInfos)
            sb.Append($"{source} {target} {fsType} {options} 0 0\n");
        return sb.ToString();
    }

    public static string GenerateCpuInfo()
    {
        return
            "processor\t: 0\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t: 1\nmodel name\t: x86emu\nstepping\t: 1\nmicrocode\t: 0x1\ncpu MHz\t\t: 1000.000\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 1\ncore id\t\t: 0\ncpu cores\t: 1\napicid\t\t: 0\ninitial apicid\t: 0\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 1\nwp\t\t: yes\nflags\t\t: fpu tsc cx8 cmov mmx fxsr sse sse2\n\n";
    }

    public static string GenerateMemInfo()
    {
        return
            "MemTotal:        262144 kB\nMemFree:         131072 kB\nBuffers:              0 kB\nCached:               0 kB\nSwapCached:           0 kB\nActive:               0 kB\nInactive:             0 kB\nActive(anon):         0 kB\nInactive(anon):       0 kB\nActive(file):         0 kB\nInactive(file):       0 kB\nUnevictable:          0 kB\nMlocked:              0 kB\nSwapTotal:            0 kB\nSwapFree:             0 kB\nDirty:                0 kB\nWriteback:            0 kB\nAnonPages:            0 kB\nMapped:               0 kB\nShmem:                0 kB\nSlab:                 0 kB\nSReclaimable:         0 kB\nSUnreclaim:           0 kB\nKernelStack:          0 kB\nPageTables:           0 kB\nNFS_Unstable:         0 kB\nBounce:               0 kB\nWritebackTmp:         0 kB\nCommitLimit:     131072 kB\nCommitted_AS:         0 kB\nVmallocTotal:   34359738367 kB\nVmallocUsed:          0 kB\nVmallocChunk:   34359738367 kB\nHardwareCorrupted:     0 kB\nAnonHugePages:        0 kB\nHugePages_Total:       0\nHugePages_Free:        0\nHugePages_Rsvd:        0\nHugePages_Surp:        0\nHugepagesize:       2048 kB\nDirectMap4k:       4096 kB\nDirectMap2M:     258048 kB\n";
    }

    public static string GenerateVersion()
    {
        return
            "Linux version 6.1.0-fiberish (builder@bot) (gcc version 12.2.0 (Debian 12.2.0-14)) #1 SMP PREEMPT\n";
    }

    public static string GenerateMountInfo(SyscallManager? sm)
    {
        if (sm == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var m in sm.MountInfoEntries)
        {
            // Minimal mountinfo format:
            // id parent major:minor root mountpoint options - fstype source superopts
            var mountPoint = EscapeMountField(m.Target);
            var source = EscapeMountField(m.Source);
            var options = string.IsNullOrWhiteSpace(m.Options) ? "rw,relatime" : m.Options;
            sb.Append(m.Id)
                .Append(' ')
                .Append(m.ParentId)
                .Append(" 0:0 / ")
                .Append(mountPoint)
                .Append(' ')
                .Append(options)
                .Append(" - ")
                .Append(m.FsType)
                .Append(' ')
                .Append(source)
                .Append(' ')
                .Append(options)
                .Append('\n');
        }

        return sb.ToString();
    }

    public static string GenerateSystemStat(KernelScheduler? scheduler)
    {
        var processes = scheduler?.GetProcessesSnapshot() ?? [];
        var running = processes.Count(p => p.State == ProcessState.Running);
        var total = processes.Count;
        var uptimeSeconds = (scheduler?.CurrentTick ?? 0) / 1000;
        var btime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - uptimeSeconds;

        // Keep it minimal but compatible with common parsers.
        return
            $"cpu  0 0 0 0 0 0 0 0 0 0\n" +
            $"cpu0 0 0 0 0 0 0 0 0 0 0\n" +
            $"intr 0\n" +
            $"ctxt 0\n" +
            $"btime {btime}\n" +
            $"processes {total}\n" +
            $"procs_running {running}\n" +
            $"procs_blocked 0\n";
    }

    public static string GenerateUptime(KernelScheduler? scheduler)
    {
        var seconds = Math.Max(0, (scheduler?.CurrentTick ?? 0) / 1000.0);
        return $"{seconds:F2} {seconds:F2}\n";
    }

    public static string GenerateLoadAvg(KernelScheduler? scheduler)
    {
        var processes = scheduler?.GetProcessesSnapshot() ?? [];
        var running = Math.Max(1, processes.Count(p => p.State == ProcessState.Running));
        var total = Math.Max(1, processes.Count);

        // Minimal synthetic load averages.
        var l = (double)running / total;
        var pid = scheduler?.CurrentTask?.Process.TGID ?? 1;
        return $"{l:F2} {l:F2} {l:F2} {running}/{total} {pid}\n";
    }

    public static string GenerateSysKernelHostname(Process? process)
    {
        return $"{(process?.UTS.NodeName ?? "x86emu")}\n";
    }

    public static string GenerateSysKernelOsRelease(Process? process)
    {
        return $"{(process?.UTS.Release ?? "6.1.0")}\n";
    }

    public static string GenerateSysKernelOstype(Process? process)
    {
        return $"{(process?.UTS.SysName ?? "Linux")}\n";
    }

    public static string GenerateSysKernelVersion(Process? process)
    {
        return $"{(process?.UTS.Version ?? "#1 SMP PREEMPT")}\n";
    }

    public static string GenerateSysVmOvercommitMemory()
    {
        return "0\n";
    }

    public static string GenerateSysVmSwappiness()
    {
        return "60\n";
    }

    private static string EscapeMountField(string value)
    {
        return value
            .Replace(@"\", @"\134", StringComparison.Ordinal)
            .Replace(" ", @"\040", StringComparison.Ordinal)
            .Replace("\t", @"\011", StringComparison.Ordinal)
            .Replace("\n", @"\012", StringComparison.Ordinal);
    }
}
