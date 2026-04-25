using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

[Collection("ExternalPageManagerSerial")]
public class ExecveErrorMappingTests
{
    [Fact]
    public async Task SysExecve_WhenExecPathHitsOom_ReturnsEnomem()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        var guestRoot = ResolveGuestRootForHelloStatic();
        sm.MountRootHostfs(guestRoot);
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9201, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        var oldQuota = runtime.MemoryContext.MemoryQuotaBytes;
        try
        {
            const uint filenameAddr = 0x61000000;
            MapUserPage(mm, engine, filenameAddr);
            WriteCString(engine, filenameAddr, "/hello_static");

            // Keep one page pinned so exec's first strict allocation reliably trips the quota
            // even if the old address space releases pages before loading the new image.
            var reservedPage = runtime.MemoryContext.BackingPagePool.AllocAnonPage();
            try
            {
                runtime.MemoryContext.MemoryQuotaBytes = LinuxConstants.PageSize;

                var rc = await Call(sm, "SysExecve", filenameAddr);
                Assert.Equal(-(int)Errno.ENOMEM, rc);
            }
            finally
            {
                BackingPageHandle.Release(ref reservedPage);
            }
        }
        finally
        {
            runtime.MemoryContext.MemoryQuotaBytes = oldQuota;
            sm.Close();
        }
    }

    [Fact]
    public async Task SysExecve_WhenProcSelfFdPointsToUnlinkedMemfdScript_UsesLiveFileForShebang()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        var guestRoot = ResolveGuestRootForHelloStatic();
        sm.MountRootHostfs(guestRoot);
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9202, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        const uint nameAddr = 0x61000000;
        const uint pathAddr = 0x61001000;
        MapUserPage(mm, engine, nameAddr);
        MapUserPage(mm, engine, pathAddr);

        try
        {
            WriteCString(engine, nameAddr, "script");
            var fd = await Call(sm, "SysMemfdCreate", nameAddr);
            Assert.True(fd >= 0);
            Assert.True(sm.FDs.TryGetValue(fd, out var file));
            file!.OpenedInode!.Mode = 0x1ED;

            var script = Encoding.UTF8.GetBytes("#!/hello_static\n");
            var writeRc = file.OpenedInode.WriteFromHost(task, file, script, 0);
            Assert.Equal(script.Length, writeRc);

            var procFdPath = $"/proc/{process.TGID}/fd/{fd}";
            WriteCString(engine, pathAddr, procFdPath);
            var rc = await Call(sm, "SysExecve", pathAddr);

            Assert.Equal(0, rc);
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task SysExecve_WhenRegularFileHasNoExecuteBit_ReturnsEacces()
    {
        var root = CreateTempExecRoot();
        try
        {
            File.Copy(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"), Path.Combine(root, "noexec"),
                true);
            SetUnixMode(Path.Combine(root, "noexec"), 0x1A4); // 0644

            var runtime = new TestRuntimeFactory();
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            var process = new Process(9210, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(process.TGID, process, engine, scheduler);
            engine.Owner = task;

            const uint pathAddr = 0x61100000;
            MapUserPage(mm, engine, pathAddr);
            WriteCString(engine, pathAddr, "/noexec");

            var rc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(-(int)Errno.EACCES, rc);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SysExecve_WhenShebangInterpreterHasNoExecuteBit_ReturnsEacces()
    {
        var root = CreateTempExecRoot();
        try
        {
            File.Copy(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"), Path.Combine(root, "interp"),
                true);
            SetUnixMode(Path.Combine(root, "interp"), 0x1A4); // 0644
            await File.WriteAllTextAsync(Path.Combine(root, "script"), "#!/interp\n");
            SetUnixMode(Path.Combine(root, "script"), 0x1ED); // 0755

            var runtime = new TestRuntimeFactory();
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            var process = new Process(9211, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(process.TGID, process, engine, scheduler);
            engine.Owner = task;

            const uint pathAddr = 0x61101000;
            MapUserPage(mm, engine, pathAddr);
            WriteCString(engine, pathAddr, "/script");

            var rc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(-(int)Errno.EACCES, rc);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SysExecve_AfterPrlimitStack_MapsGrowDownStackAndCpuWriteExpandsIt()
    {
        const ulong stackSoftLimitBytes = 64UL * 1024;
        const ulong stackHardLimitBytes = 8UL * 1024 * 1024;
        using var env = ExecveEnv.Create(9212, 0x1122_3344_5566_7788);

        const uint limitPtr = 0x61102000;
        MapUserPage(env.Process.Mem, env.Engine, limitPtr);
        WriteRlimit64(env.Engine, limitPtr, stackSoftLimitBytes, stackHardLimitBytes);

        Assert.Equal(0, await Call(env.Syscalls, "SysPrlimit64", 0,
            unchecked(LinuxConstants.RLIMIT_STACK), limitPtr));
        Assert.Equal(0, await env.ExecHelloStatic());

        var layout = Assert.IsType<GuestAddressSpaceLayout>(env.Process.Mem.Layout);
        var stackVma = env.StackVma;
        Assert.Equal((uint)stackSoftLimitBytes, layout.InitialStackTop - layout.StackLowerBound);
        Assert.Equal(layout.StackLowerBound, stackVma.GrowDownLimit);
        Assert.True((stackVma.Flags & MapFlags.GrowDown) != 0);
        Assert.True((stackVma.Flags & MapFlags.Stack) != 0);

        var growAddr = stackVma.Start - LinuxConstants.PageSize;
        var payload = new byte[] { 0x5A };
        Assert.True(env.Engine.CopyToUser(growAddr, payload));
        Assert.Equal(growAddr, stackVma.Start);

        Assert.Equal([0x5A], ReadBytes(env.Engine, growAddr, 1));
    }

    [Fact]
    public async Task SysExecve_AfterExec_AutoMmapRespectsCompatMmapBaseGap()
    {
        using var env = ExecveEnv.Create(9213, 0x2233_4455_6677_8899);

        Assert.Equal(0, await env.ExecHelloStatic());

        var layout = Assert.IsType<GuestAddressSpaceLayout>(env.Process.Mem.Layout);
        var stackVma = env.StackVma;
        var mapped = AssertGuestAddressSuccess(await Call(env.Syscalls, "SysMmap2", 0, LinuxConstants.PageSize,
            (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous)));

        Assert.True(layout.MmapBase < stackVma.Start,
            $"mmap_base=0x{layout.MmapBase:x8} stack_start=0x{stackVma.Start:x8}");
        Assert.True(mapped + LinuxConstants.PageSize <= layout.MmapBase,
            $"mapped=0x{mapped:x8} mmap_base=0x{layout.MmapBase:x8}");
    }

    [Fact]
    public async Task SysExecve_GrowDownWriteBlockedByLowerFixedMappingPreservesGuardPage()
    {
        using var env = ExecveEnv.Create(9214, 0x3344_5566_7788_99aa);

        Assert.Equal(0, await env.ExecHelloStatic());

        var stackVma = env.StackVma;
        var originalStart = stackVma.Start;
        var blockerAddr = originalStart - LinuxConstants.PageSize * 2;
        var blocker = AssertGuestAddressSuccess(await Call(env.Syscalls, "SysMmap2", blockerAddr,
            LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(blockerAddr, blocker);

        var blockedAddr = originalStart - LinuxConstants.PageSize;
        Assert.False(env.Engine.CopyToUser(blockedAddr, [0x33]));
        Assert.Equal(FaultResult.Segv, env.Process.Mem.HandleFaultDetailed(blockedAddr, true, env.Engine));
        Assert.Equal(originalStart, stackVma.Start);
    }

    [Fact]
    public async Task SysExecve_WhenOverlayUpperExecutableClearsAddressSpace_ReleasesTransientExecFileHolders()
    {
#if RELEASE
        // This test relies on debug-only file holder tracking which is disabled in Release builds
        await Task.CompletedTask;
#else
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        var scheduler = new KernelScheduler();
        var process = new Process(9206, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        var silkRoot = Path.Combine(Path.GetTempPath(), $"execve-overlay-upper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(silkRoot);

        const uint pathAddr = 0x6100D000;
        MapUserPage(mm, engine, pathAddr);

        try
        {
            var tmpfsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var lowerSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext)
                .ReadSuper(tmpfsType, 0, "execve-upper-lower", null);
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var helloBytes = File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));
            sm.WriteFileInDetachedMount(sm.RootMount!, "hello_static", helloBytes, 0x1ED);

            WriteCString(engine, pathAddr, "/hello_static");
            var execRc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(0, execRc);
            Assert.NotSame(mm, process.Mem);

            var (execLoc, _) = sm.ResolvePath("/hello_static");
            var overlayInode = Assert.IsType<OverlayInode>(execLoc.Dentry!.Inode);
            var upperInode = Assert.IsAssignableFrom<MappingBackedInode>(overlayInode.UpperInode);
            var holdersAfterExec = upperInode.DescribeActiveFileHoldersForDebug();

            Assert.DoesNotContain("SysExecve", holdersAfterExec, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveExecutableImage", holdersAfterExec, StringComparison.Ordinal);

            mm.Clear(engine);

            var holdersAfterClear = upperInode.DescribeActiveFileHoldersForDebug();
            Assert.DoesNotContain("SysExecve", holdersAfterClear, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveExecutableImage", holdersAfterClear, StringComparison.Ordinal);
            Assert.True(
                holdersAfterClear.Contains("LoadSegments", StringComparison.Ordinal) ||
                holdersAfterClear.Contains("ElfLoader", StringComparison.Ordinal),
                $"Expected 'LoadSegments' or 'ElfLoader' in holders: {holdersAfterClear}");

            process.Mem.Clear(engine);

            Assert.Equal("<none>", upperInode.DescribeActiveFileHoldersForDebug());
        }
        finally
        {
            sm.Close();
            if (Directory.Exists(silkRoot))
                Directory.Delete(silkRoot, true);
        }
#endif
    }

    [Fact]
    public async Task ProcFdReadlink_WhenMemfd_DoesNotAppendDeletedSuffix()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(ResolveGuestRootForHelloStatic());
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9203, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        const uint nameAddr = 0x61002000;
        const uint pathAddr = 0x61003000;
        const uint bufAddr = 0x61004000;
        MapUserPage(mm, engine, nameAddr);
        MapUserPage(mm, engine, pathAddr);
        MapUserPage(mm, engine, bufAddr);

        try
        {
            WriteCString(engine, nameAddr, "readlink-memfd");
            var fd = await Call(sm, "SysMemfdCreate", nameAddr);
            Assert.True(fd >= 0);

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var rc = await Call(sm, "SysReadlink", pathAddr, bufAddr, 256);

            Assert.True(rc > 0);
            var target = Encoding.UTF8.GetString(ReadBytes(engine, bufAddr, rc));
            Assert.Contains("readlink-memfd", target);
            Assert.False(target.EndsWith(" (deleted)", StringComparison.Ordinal));
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task ProcFdReadlink_WhenMemfdNameContainsSlash_PreservesNameAndCreateSucceeds()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(ResolveGuestRootForHelloStatic());
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9206, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        const uint nameAddr = 0x6100D000;
        const uint pathAddr = 0x6100E000;
        const uint bufAddr = 0x6100F000;
        MapUserPage(mm, engine, nameAddr);
        MapUserPage(mm, engine, pathAddr);
        MapUserPage(mm, engine, bufAddr);

        try
        {
            WriteCString(engine, nameAddr, "apk/tmp.trigger");
            var fd = await Call(sm, "SysMemfdCreate", nameAddr);
            Assert.True(fd >= 0);

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var rc = await Call(sm, "SysReadlink", pathAddr, bufAddr, 256);

            Assert.True(rc > 0);
            var target = Encoding.UTF8.GetString(ReadBytes(engine, bufAddr, rc));
            Assert.Contains("/memfd:apk/tmp.trigger", target, StringComparison.Ordinal);
            Assert.False(target.EndsWith(" (deleted)", StringComparison.Ordinal));
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task ProcFdOpen_CreatesNewFileDescriptionWithIndependentOffset()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(ResolveGuestRootForHelloStatic());
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9204, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        const uint nameAddr = 0x61005000;
        const uint pathAddr = 0x61006000;
        const uint buf1Addr = 0x61007000;
        const uint buf2Addr = 0x61008000;
        const uint buf3Addr = 0x61009000;
        MapUserPage(mm, engine, nameAddr);
        MapUserPage(mm, engine, pathAddr);
        MapUserPage(mm, engine, buf1Addr);
        MapUserPage(mm, engine, buf2Addr);
        MapUserPage(mm, engine, buf3Addr);

        try
        {
            WriteCString(engine, nameAddr, "open-memfd");
            var fd = await Call(sm, "SysMemfdCreate", nameAddr);
            Assert.True(fd >= 0);
            Assert.True(sm.FDs.TryGetValue(fd, out var originalFile));

            var payload = Encoding.UTF8.GetBytes("abc");
            var writeRc = originalFile!.OpenedInode!.WriteFromHost(task, originalFile, payload, 0);
            Assert.Equal(payload.Length, writeRc);

            Assert.Equal(0, await Call(sm, "SysLseek", (uint)fd));
            Assert.Equal(1, await Call(sm, "SysRead", (uint)fd, buf1Addr, 1));
            Assert.Equal("a", Encoding.UTF8.GetString(ReadBytes(engine, buf1Addr, 1)));

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var reopenedFd = await Call(sm, "SysOpen", pathAddr);
            Assert.True(reopenedFd >= 0);
            Assert.NotEqual(fd, reopenedFd);
            Assert.NotSame(sm.GetFD(fd), sm.GetFD(reopenedFd));

            Assert.Equal(1, await Call(sm, "SysRead", (uint)reopenedFd, buf2Addr, 1));
            Assert.Equal("a", Encoding.UTF8.GetString(ReadBytes(engine, buf2Addr, 1)));

            Assert.Equal(1, await Call(sm, "SysRead", (uint)fd, buf3Addr, 1));
            Assert.Equal("b", Encoding.UTF8.GetString(ReadBytes(engine, buf3Addr, 1)));
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task ProcFdOpen_RechecksPermissionsInsteadOfReusingOriginalOpenFile()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(ResolveGuestRootForHelloStatic());
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9205, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        const uint nameAddr = 0x6100A000;
        const uint pathAddr = 0x6100B000;
        const uint bufAddr = 0x6100C000;
        MapUserPage(mm, engine, nameAddr);
        MapUserPage(mm, engine, pathAddr);
        MapUserPage(mm, engine, bufAddr);

        try
        {
            WriteCString(engine, nameAddr, "perm-memfd");
            var fd = await Call(sm, "SysMemfdCreate", nameAddr);
            Assert.True(fd >= 0);
            Assert.True(sm.FDs.TryGetValue(fd, out var file));

            var payload = Encoding.UTF8.GetBytes("z");
            Assert.Equal(payload.Length, file!.OpenedInode!.WriteFromHost(task, file, payload, 0));
            Assert.Equal(0, await Call(sm, "SysLseek", (uint)fd));

            process.UID = process.EUID = process.SUID = process.FSUID = 1000;
            process.GID = process.EGID = process.SGID = process.FSGID = 1000;

            Assert.Equal(1, await Call(sm, "SysRead", (uint)fd, bufAddr, 1));
            Assert.Equal("z", Encoding.UTF8.GetString(ReadBytes(engine, bufAddr, 1)));

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var reopenedFd = await Call(sm, "SysOpen", pathAddr);
            Assert.Equal(-(int)Errno.EACCES, reopenedFd);
        }
        finally
        {
            sm.Close();
        }
    }

    private static void MapUserPage(VMAManager mm, Engine engine, uint addr)
    {
        mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
            engine);
        Assert.True(mm.HandleFault(addr, true, engine));
    }

    private static void WriteCString(Engine engine, uint addr, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Assert.True(engine.CopyToUser(addr, bytes));
    }

    private static byte[] ReadBytes(Engine engine, uint addr, int length)
    {
        var bytes = new byte[length];
        Assert.True(engine.CopyFromUser(addr, bytes));
        return bytes;
    }

    private static void WriteRlimit64(Engine engine, uint addr, ulong soft, ulong hard)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), soft);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), hard);
        Assert.True(engine.CopyToUser(addr, bytes));
    }

    private static uint AssertGuestAddressSuccess(int rc)
    {
        var raw = unchecked((uint)rc);
        Assert.True(raw < 0xFFFFF000u, $"expected guest address result, got rc={rc} raw=0x{raw:x8}");
        return raw;
    }

    private static async ValueTask<int> Call(SyscallManager sm, string methodName, uint a1 = 0, uint a2 = 0,
        uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var previous = sm.CurrentSyscallEngine.CurrentSyscallManager;
        sm.CurrentSyscallEngine.CurrentSyscallManager = sm;
        try
        {
            var task = (ValueTask<int>)method!.Invoke(sm, [sm.CurrentSyscallEngine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
        finally
        {
            sm.CurrentSyscallEngine.CurrentSyscallManager = previous;
        }
    }

    private static string ResolveGuestRootForHelloStatic()
    {
        const string rel = "tests/linux/hello_static";
        var cwd = Directory.GetCurrentDirectory();
        var current = new DirectoryInfo(cwd);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, rel);
            if (File.Exists(candidate))
                return Path.Combine(current.FullName, "tests/linux");
            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/linux/hello_static from test working directory.");
    }

    private static string CreateTempExecRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"execve-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SetUnixMode(string path, int mode)
    {
#pragma warning disable CA1416
        File.SetUnixFileMode(path, (UnixFileMode)mode);
#pragma warning restore CA1416
    }

    private sealed class ExecveEnv : IDisposable
    {
        private readonly uint _pathAddr = 0x61100000;

        private ExecveEnv(TestRuntimeFactory runtime, Engine engine, VMAManager mm, SyscallManager syscalls,
            KernelScheduler scheduler, Process process, FiberTask task)
        {
            Runtime = runtime;
            Engine = engine;
            Memory = mm;
            Syscalls = syscalls;
            Scheduler = scheduler;
            Process = process;
            Task = task;
        }

        public TestRuntimeFactory Runtime { get; }
        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager Syscalls { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public VmArea StackVma => Assert.Single(Process.Mem.VMAs.Where(v => v.Name == "STACK"));

        public void Dispose()
        {
            Syscalls.Close();
            Engine.Dispose();
        }

        public async ValueTask<int> ExecHelloStatic()
        {
            MapUserPage(Process.Mem, Engine, _pathAddr);
            WriteCString(Engine, _pathAddr, "/hello_static");
            return await Call(Syscalls, "SysExecve", _pathAddr);
        }

        public static ExecveEnv Create(int pid, ulong deterministicAslrSeed)
        {
            var runtime = new TestRuntimeFactory(deterministicAslrSeed);
            var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var syscalls = new SyscallManager(engine, mm, 0);
            syscalls.MountRootHostfs(ResolveGuestRootForHelloStatic());
            syscalls.MountStandardProc();

            var scheduler = new KernelScheduler();
            var process = new Process(pid, mm, syscalls);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(process.TGID, process, engine, scheduler);
            engine.Owner = task;

            return new ExecveEnv(runtime, engine, mm, syscalls, scheduler, process, task);
        }
    }
}
