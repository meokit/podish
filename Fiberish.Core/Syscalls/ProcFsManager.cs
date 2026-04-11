using System.Text;
using Fiberish.Core;
using Fiberish.Memory;

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
            _ = loc.Dentry!.TryUncacheChild(pidStr, "ProcFsManager.OnProcessExit", out _);
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
        var ttyNr = 0;
        var tpgid = -1;
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

    public static string GenerateMemInfo(SyscallManager? sm)
    {
        var snapshot = sm?.MemoryContext.CaptureMemoryStats(sm) ?? default;
        var totalKb = ToKiB(snapshot.MemTotalBytes);
        var freeKb = ToKiB(snapshot.FreeBytes);
        var availableKb = ToKiB(snapshot.MemAvailableBytes);
        var cachedKb = ToKiB(snapshot.CachedBytes);
        var dirtyKb = ToKiB(snapshot.DirtyBytes);
        var writebackKb = ToKiB(snapshot.WritebackBytes);
        var anonKb = ToKiB(snapshot.AnonPagesBytes);
        var mappedKb = ToKiB(snapshot.MappedBytes);
        var shmemKb = ToKiB(snapshot.ShmemBytes);
        var hostMappedKb = ToKiB(snapshot.HostMappedWindowBytes);
        var reclaimableKb = ToKiB(snapshot.ReclaimableBytes);
        var activeKb = ToKiB(snapshot.ActiveBytes);
        var inactiveKb = ToKiB(snapshot.InactiveBytes);
        var activeAnonKb = ToKiB(snapshot.ActiveAnonBytes);
        var inactiveAnonKb = ToKiB(snapshot.InactiveAnonBytes);
        var activeFileKb = ToKiB(snapshot.ActiveFileBytes);
        var inactiveFileKb = ToKiB(snapshot.InactiveFileBytes);

        var commitLimitKb = totalKb;
        var committedKb = ToKiB(snapshot.CommittedBytes);
        var directMap2MKb = Math.Max(0, totalKb - 4096);

        return
            $"MemTotal:{Fmt(totalKb)} kB\n" +
            $"MemFree:{Fmt(freeKb)} kB\n" +
            $"MemAvailable:{Fmt(availableKb)} kB\n" +
            "Buffers:              0 kB\n" +
            $"Cached:{Fmt(cachedKb)} kB\n" +
            "SwapCached:           0 kB\n" +
            $"Active:{Fmt(activeKb)} kB\n" +
            $"Inactive:{Fmt(inactiveKb)} kB\n" +
            $"Active(anon):{Fmt(activeAnonKb)} kB\n" +
            $"Inactive(anon):{Fmt(inactiveAnonKb)} kB\n" +
            $"Active(file):{Fmt(activeFileKb)} kB\n" +
            $"Inactive(file):{Fmt(inactiveFileKb)} kB\n" +
            "Unevictable:          0 kB\n" +
            "Mlocked:              0 kB\n" +
            "SwapTotal:            0 kB\n" +
            "SwapFree:             0 kB\n" +
            $"Dirty:{Fmt(dirtyKb)} kB\n" +
            $"Writeback:{Fmt(writebackKb)} kB\n" +
            $"AnonPages:{Fmt(anonKb)} kB\n" +
            $"Mapped:{Fmt(mappedKb)} kB\n" +
            $"HostMapped:{Fmt(hostMappedKb)} kB\n" +
            $"Shmem:{Fmt(shmemKb)} kB\n" +
            "Slab:                0 kB\n" +
            "SReclaimable:         0 kB\n" +
            "SUnreclaim:           0 kB\n" +
            "KernelStack:          0 kB\n" +
            "PageTables:           0 kB\n" +
            "NFS_Unstable:         0 kB\n" +
            "Bounce:               0 kB\n" +
            "WritebackTmp:         0 kB\n" +
            $"CommitLimit:{Fmt(commitLimitKb)} kB\n" +
            $"Committed_AS:{Fmt(committedKb)} kB\n" +
            "VmallocTotal:   34359738367 kB\n" +
            "VmallocUsed:          0 kB\n" +
            "VmallocChunk:   34359738367 kB\n" +
            "HardwareCorrupted:    0 kB\n" +
            "AnonHugePages:        0 kB\n" +
            "HugePages_Total:       0\n" +
            "HugePages_Free:        0\n" +
            "HugePages_Rsvd:        0\n" +
            "HugePages_Surp:        0\n" +
            "Hugepagesize:       2048 kB\n" +
            "DirectMap4k:       4096 kB\n" +
            $"DirectMap2M:{Fmt(directMap2MKb)} kB\n";
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
        return $"{process?.UTS.NodeName ?? "x86emu"}\n";
    }

    public static string GenerateSysKernelOsRelease(Process? process)
    {
        return $"{process?.UTS.Release ?? "6.1.0"}\n";
    }

    public static string GenerateSysKernelOstype(Process? process)
    {
        return $"{process?.UTS.SysName ?? "Linux"}\n";
    }

    public static string GenerateSysKernelVersion(Process? process)
    {
        return $"{process?.UTS.Version ?? "#1 SMP PREEMPT"}\n";
    }

    public static string GenerateSysVmOvercommitMemory()
    {
        return "0\n";
    }

    public static string GenerateSysVmSwappiness()
    {
        return "60\n";
    }

    public static string GenerateSysVmDropCaches()
    {
        // Linux exposes this control as a write-triggered knob and reads as 0.
        return "0\n";
    }

    private static string EscapeMountField(string value)
    {
        return value
            .Replace(@"\", @"\134", StringComparison.Ordinal)
            .Replace(" ", @"\040", StringComparison.Ordinal)
            .Replace("\t", @"\011", StringComparison.Ordinal)
            .Replace("\n", @"\012", StringComparison.Ordinal);
    }

    private static long ToKiB(long bytes)
    {
        if (bytes <= 0) return 0;
        return bytes / 1024;
    }

    private static string Fmt(long kib)
    {
        return string.Format("{0,15}", kib);
    }
}
