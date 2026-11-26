using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public static class ProcFsManager
{
    private static char GetProcessState(Process process)
    {
        return process.State switch
        {
            ProcessState.Stopped => 'T',
            ProcessState.Zombie => 'Z',
            ProcessState.Dead => 'X',
            ProcessState.Sleeping => 'S',
            _ => GetProcessTaskState(process)
        };
    }

    private static char GetProcessTaskState(Process process)
    {
        var leader = process.Threads.FirstOrDefault(t => t.TID == process.TGID) ?? process.Threads.FirstOrDefault();
        if (leader != null)
        {
            return leader.Status switch
            {
                FiberTaskStatus.Waiting => 'S',
                FiberTaskStatus.Zombie => 'Z',
                FiberTaskStatus.Terminated => 'X',
                _ => 'R'
            };
        }

        return 'R';
    }

    public static char GetTaskState(FiberTask task)
    {
        return task.Status switch
        {
            FiberTaskStatus.Waiting => 'S',
            FiberTaskStatus.Zombie => 'Z',
            FiberTaskStatus.Terminated => 'X',
            _ => 'R'
        };
    }

    public static string GenerateThreadStat(FiberTask task, Process process)
    {
        var stateChar = GetTaskState(task);
        var ttyNr = 0;
        var tpgid = -1;
        if (process.ControllingTty != null)
        {
            ttyNr = 34816;
            tpgid = process.ControllingTty.ForegroundPgrp;
        }

        var selfCpu = process.GetSelfCpuTimeSnapshot();
        var childrenCpu = process.GetChildrenCpuTimeSnapshot();
        var utime = SyscallManager.NanosecondsToUserClockTicks(selfCpu.UserNs);
        var stime = SyscallManager.NanosecondsToUserClockTicks(selfCpu.SystemNs);
        var cutime = SyscallManager.NanosecondsToUserClockTicks(childrenCpu.UserNs);
        var cstime = SyscallManager.NanosecondsToUserClockTicks(childrenCpu.SystemNs);

        return
            $"{task.TID} ({process.Comm}) {stateChar} {process.PPID} {process.PGID} {process.SID} {ttyNr} {tpgid} 0 0 0 0 0 {utime} {stime} {cutime} {cstime} 0 0 {process.Threads.Count} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
    }

    public static string GenerateThreadStatus(FiberTask task, Process process)
    {
        var stateChar = GetTaskState(task);
        var stateMsg = stateChar switch
        {
            'R' => "R (running)",
            'S' => "S (sleeping)",
            'T' => "T (stopped)",
            'Z' => "Z (zombie)",
            'X' => "X (dead)",
            _ => "R (running)"
        };
        return $"Name:\t{process.Comm}\nState:\t{stateMsg}\nTgid:\t{process.TGID}\nPid:\t{task.TID}\nPPid:\t{process.PPID}\n";
    }

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
        var stateChar = GetProcessState(process);
        var stateMsg = stateChar switch
        {
            'R' => "R (running)",
            'S' => "S (sleeping)",
            'T' => "T (stopped)",
            'Z' => "Z (zombie)",
            'X' => "X (dead)",
            _ => "R (running)"
        };

        var lockedKb = 0L;
        var vmSizeKb = 0L;
        var vmRssKb = 0L;
        var vmas = process.Mem.VMAs;
        for (var i = 0; i < vmas.Count; i++)
        {
            var vma = vmas[i];
            var vmaKb = (long)(vma.Length / 1024);
            vmSizeKb += vmaKb;
            vmRssKb += CountVmaResidentKb(process, vma);
            if ((vma.Flags & MapFlags.Locked) != 0)
                lockedKb += vmaKb;
        }

        return $"Name:\t{process.Comm}\nState:\t{stateMsg}\nPid:\t{process.TGID}\nPPid:\t{process.PPID}\nVmSize:\t{vmSizeKb,8} kB\nVmLck:\t{lockedKb,8} kB\nVmRSS:\t{vmRssKb,8} kB\n";
    }

    public static string GenerateCmdline(Process process)
    {
        return Encoding.UTF8.GetString(process.CommandLineRaw);
    }

    public static string GenerateStat(Process process)
    {
        // Minimal stat for ps
        // pid (comm) state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime priority nice num_threads itrealvalue starttime vsize rss rsslim startcode endcode startstack kstkesp kstkeip signal blocked sigignore sigcatch wchan nswap cnswap exit_signal processor rt_priority policy

        var stateChar = GetProcessState(process);

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

        var selfCpu = process.GetSelfCpuTimeSnapshot();
        var childrenCpu = process.GetChildrenCpuTimeSnapshot();
        var utime = SyscallManager.NanosecondsToUserClockTicks(selfCpu.UserNs);
        var stime = SyscallManager.NanosecondsToUserClockTicks(selfCpu.SystemNs);
        var cutime = SyscallManager.NanosecondsToUserClockTicks(childrenCpu.UserNs);
        var cstime = SyscallManager.NanosecondsToUserClockTicks(childrenCpu.SystemNs);

        return
            $"{process.TGID} ({process.Comm}) {stateChar} {process.PPID} {process.PGID} {process.SID} {ttyNr} {tpgid} 0 0 0 0 0 {utime} {stime} {cutime} {cstime} 0 0 {process.Threads.Count} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
    }

    public static string GenerateMounts(SyscallManager? sm)
    {
        if (sm == null) return string.Empty;
        return GenerateMounts(sm.MountNamespace, sm.ProcessRoot);
    }

    internal static string GenerateMounts(MountNamespace mountNamespace, PathLocation root)
    {
        var sb = new StringBuilder();
        foreach (var m in mountNamespace.GetMountInfoEntries(root))
            sb.Append($"{EscapeMountField(m.Source)} {EscapeMountField(m.Target)} {m.FsType} {m.Options} 0 0\n");
        return sb.ToString();
    }

    public static string GenerateCpuInfo() => GenerateCpuInfo(false);

    internal static string GenerateCpuInfo(bool isAArch64)
    {
        if (isAArch64)
            return "processor\t: 0\nmodel name\t: AArch64\nCPU architecture: 8\n\n";

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
        return GenerateMountInfo(sm.MountNamespace, sm.ProcessRoot);
    }

    internal static string GenerateMountInfo(MountNamespace mountNamespace, PathLocation root)
    {
        var sb = new StringBuilder();
        foreach (var m in mountNamespace.GetMountInfoEntries(root))
        {
            // Minimal mountinfo format:
            // id parent major:minor root mountpoint options - fstype source superopts
            var mountPoint = EscapeMountField(m.Target);
            var source = EscapeMountField(m.Source);
            var options = string.IsNullOrWhiteSpace(m.Options) ? "rw,relatime" : m.Options;
            var devMajor = (m.Dev >> 8) & 0xFF;
            var devMinor = m.Dev & 0xFF;
            sb.Append(m.Id)
                .Append(' ')
                .Append(m.ParentId)
                .Append(' ')
                .Append(devMajor)
                .Append(':')
                .Append(devMinor)
                .Append(' ')
                .Append(EscapeMountField(m.Root))
                .Append(' ')
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

    private static void AppendVmaHeaderLine(StringBuilder sb, VmArea vma)
    {
        var dev = vma.LogicalInode?.SuperBlock?.Dev ?? 0;
        var ino = vma.LogicalInode?.Ino ?? 0;
        sb.Append($"{vma.Start:x8}-{vma.End:x8} ");
        sb.Append((vma.Perms & Protection.Read) != 0 ? 'r' : '-');
        sb.Append((vma.Perms & Protection.Write) != 0 ? 'w' : '-');
        sb.Append((vma.Perms & Protection.Exec) != 0 ? 'x' : '-');
        sb.Append((vma.Flags & MapFlags.Shared) != 0 ? 's' : 'p');
        sb.Append($" {vma.Offset:x8} {dev >> 20:x2}:{dev & 0xFFFFF:x2} {ino,-8} {vma.Name}\n");
    }

    private static long CountVmaResidentKb(Process process, VmArea vma)
    {
        var pageMapping = process.Mem.PageMapping;
        var anon = vma.VmAnonVma;
        var anonCount = anon?.PageCount ?? 0;
        if (pageMapping.Count == 0 && anonCount == 0)
            return 0;

        var vmaPageCount = vma.Length / LinuxConstants.PageSize;
        if (vmaPageCount > (ulong)pageMapping.Count * 2 + (ulong)anonCount)
        {
            var residentPages = pageMapping.CountInRange(vma.Start, vma.End);
            if (anon != null)
            {
                var first = vma.GetPageIndex(vma.Start);
                var last = vma.GetPageIndex(vma.End);
                anon.Pages.VisitResidentPagesInRange(first, last, (index, page) =>
                {
                    if (!pageMapping.TryGet(vma.GetGuestPageStart(index), out _))
                        residentPages++;
                });
            }
            return residentPages * 4L;
        }

        var residentKb = 0L;
        for (var p = vma.Start; p < vma.End; p += LinuxConstants.PageSize)
        {
            if (pageMapping.TryGet(p, out _))
            {
                residentKb += 4;
                continue;
            }

            if (anonCount > 0 && anon!.PeekPage(vma.GetPageIndex(p)) != IntPtr.Zero)
                residentKb += 4;
        }
        return residentKb;
    }

    public static string GenerateMaps(Process? process)
    {
        if (process == null) return string.Empty;
        var sb = new StringBuilder();
        var vmas = process.Mem.VMAs;
        for (var i = 0; i < vmas.Count; i++)
            AppendVmaHeaderLine(sb, vmas[i]);
        return sb.ToString();
    }

    public static string GenerateSmaps(Process? process)
    {
        if (process == null) return string.Empty;
        var sb = new StringBuilder();
        var vmas = process.Mem.VMAs;
        for (var i = 0; i < vmas.Count; i++)
        {
            var vma = vmas[i];
            var sizeKb = vma.Length / 1024;
            var isLocked = (vma.Flags & MapFlags.Locked) != 0;
            var residentKb = CountVmaResidentKb(process, vma);
            var lockedKb = isLocked ? residentKb : 0;

            AppendVmaHeaderLine(sb, vma);
            sb.Append($"Size:               {sizeKb,8} kB\n");
            sb.Append("KernelPageSize:            4 kB\n");
            sb.Append("MMUPageSize:               4 kB\n");
            sb.Append($"Rss:                {residentKb,8} kB\n");
            sb.Append($"Pss:                {residentKb,8} kB\n");
            sb.Append("Shared_Clean:              0 kB\n");
            sb.Append("Shared_Dirty:              0 kB\n");
            sb.Append("Private_Clean:             0 kB\n");
            sb.Append($"Private_Dirty:      {residentKb,8} kB\n");
            sb.Append($"Referenced:         {residentKb,8} kB\n");
            var anonKb = !vma.IsFileBacked && (vma.Flags & MapFlags.Shared) == 0 ? residentKb : 0;
            sb.Append($"Anonymous:          {anonKb,8} kB\n");
            sb.Append("LazyFree:                  0 kB\n");
            sb.Append("AnonHugePages:             0 kB\n");
            sb.Append("ShmemPmdMapped:            0 kB\n");
            sb.Append("FilePmdMapped:             0 kB\n");
            sb.Append("Shared_Hugetlb:            0 kB\n");
            sb.Append("Private_Hugetlb:           0 kB\n");
            sb.Append($"Locked:             {lockedKb,8} kB\n");
            sb.Append("VmFlags: rd wr mr mw me ac\n");
        }
        return sb.ToString();
    }

    public static int ReadPagemap(Process? process, long offset, Span<byte> destination)
    {
        if (process == null || destination.Length <= 0 || offset < 0) return 0;
        var taskSize = process.Mem.Layout?.TaskSize ??
                       (process.Threads.FirstOrDefault()?.CPU.IsAArch64 == true
                           ? LinuxConstants.TaskSizeAArch64
                           : LinuxConstants.TaskSize32);
        var maxBytes = (long)((taskSize / LinuxConstants.PageSize) * 8);
        if (offset >= maxBytes) return 0;

        var readable = (int)Math.Min(destination.Length, maxBytes - offset);
        var slice = destination[..readable];
        slice.Clear();

        var startPage = (uint)(offset / 8);
        var pageCount = (readable + 7) / 8;

        for (var i = 0; i < pageCount; i++)
        {
            var pageIndex = startPage + (uint)i;
            var pageAddr = (ulong)pageIndex * LinuxConstants.PageSize;
            if (pageAddr >= taskSize) break;

            var vma = process.Mem.FindVmArea(pageAddr);
            var isPresent = false;
            if (vma != null)
            {
                if (process.Mem.PageMapping.TryGet(pageAddr, out _))
                    isPresent = true;
                else if (vma.VmAnonVma is { } anon && anon.PeekPage(vma.GetPageIndex(pageAddr)) != IntPtr.Zero)
                    isPresent = true;
            }

            if (isPresent)
            {
                var entry = 1UL << 63; // PAGE_PRESENT
                var byteOffset = i * 8;
                if (byteOffset + 8 <= slice.Length)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(slice.Slice(byteOffset, 8), entry);
            }
        }

        return readable;
    }

    public static byte[] ReadPagemap(Process? process, long offset, int length)
    {
        if (process == null || length <= 0 || offset < 0) return [];
        var result = new byte[length];
        var read = ReadPagemap(process, offset, result.AsSpan());
        return read == length ? result : result[..read];
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
