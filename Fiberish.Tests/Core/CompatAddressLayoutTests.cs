using System.Buffers.Binary;
using System.Text;
using Fiberish.Core;
using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

[Collection("ExternalPageManagerSerial")]
public sealed class CompatAddressLayoutTests
{
    private const string DynamicMainRelativeName = "pie-main.elf";
    private const string DynamicMainPath = "/" + DynamicMainRelativeName;
    private const string DynamicInterpreterRelativeName = "ld-test.so";
    private const string DynamicInterpreterPath = "/" + DynamicInterpreterRelativeName;
    private const uint DynamicMainEntryOffset = 0x40;
    private const uint DynamicInterpreterEntryOffset = 0x20;
    private const uint DynamicLoadOffset = 0x1000;
    private const uint DynamicCodeSize = 0x100;

    [Fact]
    public void VmManagerClone_PreservesLayout()
    {
        var runtime = new TestRuntimeFactory();
        var mm = runtime.CreateAddressSpace();
        var random = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        mm.Layout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(64 * 1024, LinuxConstants.RLIM64_INFINITY),
            random);

        var clone = mm.Clone();

        Assert.NotNull(clone.Layout);
        Assert.NotSame(mm.Layout, clone.Layout);
        Assert.Equal(mm.Layout!.TaskSize, clone.Layout!.TaskSize);
        Assert.Equal(mm.Layout.MmapBase, clone.Layout.MmapBase);
        Assert.Equal(mm.Layout.InitialStackTop, clone.Layout.InitialStackTop);
        Assert.Equal(mm.Layout.AuxRandomBytes, clone.Layout.AuxRandomBytes);
    }

    [Fact]
    public void LoadExecutable_PlacesVdsoAtLayoutHint_AndKeepsAutoMmapBelowMmapBase()
    {
        using var env = LoadedProcessEnv.Create();

        Assert.NotNull(env.Process.Mem.Layout);
        var layout = env.Process.Mem.Layout!;
        Assert.Equal(layout.VdsoBaseHint, env.Syscalls.SigReturnAddr);
        Assert.Equal(layout.VdsoBaseHint + 16, env.Syscalls.RtSigReturnAddr);

        var mapped = env.Process.Mem.Mmap(0, LinuxConstants.PageSize, Protection.Read,
            MapFlags.Private | MapFlags.Anonymous, null, 0, "[auto-mmap-test]", env.Engine);

        Assert.True(mapped + LinuxConstants.PageSize <= layout.MmapBase,
            $"mapped=0x{mapped:x8} mmap_base=0x{layout.MmapBase:x8}");
    }

    [Fact]
    public void LoadExecutable_StackMapping_RespectsRlimitStack_AndGrowsDown()
    {
        const ulong stackLimitBytes = 64 * 1024;
        using var env = LoadedProcessEnv.Create(new ResourceLimit(stackLimitBytes, LinuxConstants.RLIM64_INFINITY));

        Assert.NotNull(env.Process.Mem.Layout);
        var layout = env.Process.Mem.Layout!;
        var stackVma = Assert.Single(env.Process.Mem.VMAs.Where(v => v.Name == "STACK"));

        Assert.True((stackVma.Flags & MapFlags.GrowDown) != 0);
        Assert.True((stackVma.Flags & MapFlags.Stack) != 0);
        Assert.True(stackVma.Length <= stackLimitBytes,
            $"stack_len=0x{stackVma.Length:x} limit=0x{stackLimitBytes:x}");
        Assert.True(stackVma.Start > layout.StackLowerBound,
            $"stack_start=0x{stackVma.Start:x8} lower=0x{layout.StackLowerBound:x8}");

        var oldStart = stackVma.Start;
        var growAddr = oldStart - LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Process.Mem.HandleFaultDetailed(growAddr, true, env.Engine));
        Assert.Equal(growAddr, stackVma.Start);
        Assert.True(stackVma.Start >= layout.StackLowerBound);
    }

    [Fact]
    public void VmaMmap_UsesLayoutTaskSizeUpperBound()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.Layout = CreateLayoutWithTaskSize(0x20000000);

        Assert.Throws<OutOfMemoryException>(() => mm.Mmap(
            0x1ffff000,
            LinuxConstants.PageSize * 2,
            Protection.Read,
            MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed,
            null,
            0,
            "[layout-bound]",
            engine));
    }

    [Fact]
    public async Task SysBrk_UsesLayoutTaskSizeUpperBound()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var syscalls = new SyscallManager(engine, mm, 0x100000);
        try
        {
            mm.Layout = CreateLayoutWithTaskSize(0x20000000);
            var method = typeof(SyscallManager).GetMethod("SysBrk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var rc = await (ValueTask<int>)method!.Invoke(syscalls, [engine, 0x20000010u, 0u, 0u, 0u, 0u, 0u])!;

            Assert.Equal(0x100000, rc);
            Assert.Equal(0x100000u, syscalls.BrkAddr);
        }
        finally
        {
            syscalls.Close();
        }
    }

    [Fact]
    public void LoadExecutable_WithDeterministicAslrSeed_ReplaysSameFirstExecAcrossContexts()
    {
        using var env1 = LoadedProcessEnv.Create(deterministicAslrSeed: 0x1234_5678_9abc_def0);
        using var env2 = LoadedProcessEnv.Create(deterministicAslrSeed: 0x1234_5678_9abc_def0);

        var layout1 = env1.Process.Mem.Layout!;
        var layout2 = env2.Process.Mem.Layout!;
        Assert.Equal(layout1.InitialStackTop, layout2.InitialStackTop);
        Assert.Equal(layout1.MmapBase, layout2.MmapBase);
        Assert.Equal(layout1.PieBase, layout2.PieBase);
        Assert.Equal(layout1.VdsoBaseHint, layout2.VdsoBaseHint);
        Assert.Equal(layout1.AuxRandomBytes, layout2.AuxRandomBytes);
    }

    [Fact]
    public void LoadExecutable_WithDeterministicAslrSeed_AdvancesExecSequenceWithinRuntime()
    {
        var memoryContext = TestRuntimeFactory.CreateDeterministicMemoryContext(0x0123_4567_89ab_cdef);

        using var env1 = LoadedProcessEnv.Create(memoryContext: memoryContext);
        using var env2 = LoadedProcessEnv.Create(memoryContext: memoryContext);

        var layout1 = env1.Process.Mem.Layout!;
        var layout2 = env2.Process.Mem.Layout!;
        Assert.NotEqual(layout1.InitialStackTop, layout2.InitialStackTop);
        Assert.NotEqual(layout1.AuxRandomBytes, layout2.AuxRandomBytes);
    }

    [Fact]
    public void GuestAddressSpaceLayout_PieAndInterpreterHints_StayWithinCompatOrdering()
    {
        var layout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 3)).ToArray());

        Assert.True(layout.LegacyMmapBase <= layout.PieBase);
        Assert.True(layout.PieBase < layout.InterpreterBaseHint);
        Assert.True(layout.InterpreterBaseHint < layout.VdsoBaseHint);
    }

    [Fact]
    public void CreateInitProcess_DynamicPie_UsesLayoutPieAndInterpreterHints()
    {
        using var runtime = KernelRuntime.BootstrapBare(false,
            memoryContext: TestRuntimeFactory.CreateDeterministicMemoryContext(0x4444_3333_2222_1111));
        MountTmpfsRoot(runtime, "compat-layout-dynamic-root");
        runtime.Syscalls.WriteFileInDetachedMount(runtime.Syscalls.RootMount!, DynamicMainRelativeName,
            BuildDynamicPieElf(DynamicInterpreterPath), 0x1ED);
        runtime.Syscalls.WriteFileInDetachedMount(runtime.Syscalls.RootMount!, DynamicInterpreterRelativeName,
            BuildDynamicInterpreterElf(), 0x1ED);

        var scheduler = new KernelScheduler();
        var (mainLoc, guestPath) = runtime.Syscalls.ResolvePath(DynamicMainPath, true);
        Assert.True(mainLoc.IsValid);
        Assert.NotNull(mainLoc.Dentry);
        Assert.NotNull(mainLoc.Mount);

        _ = ProcessFactory.CreateInitProcess(
            runtime,
            mainLoc.Dentry!,
            guestPath,
            [guestPath],
            [],
            scheduler,
            null,
            mainLoc.Mount);

        var layout = Assert.IsType<GuestAddressSpaceLayout>(runtime.Memory.Layout);
        var (interpLoc, _) = runtime.Syscalls.ResolvePath(DynamicInterpreterPath, true);
        Assert.True(interpLoc.IsValid);
        Assert.NotNull(interpLoc.Dentry);

        Assert.Contains(runtime.Memory.VMAs,
            v => ReferenceEquals(v.LogicalInode, mainLoc.Dentry!.Inode) && v.Start == layout.PieBase);
        Assert.Contains(runtime.Memory.VMAs,
            v => ReferenceEquals(v.LogicalInode, interpLoc.Dentry!.Inode) && v.Start == layout.InterpreterBaseHint);
        Assert.Equal(layout.InterpreterBaseHint + DynamicInterpreterEntryOffset, runtime.Engine.Eip);
    }

    [Fact]
    public void Exec_RepeatedExecs_AdvanceDeterministicAslrSequence()
    {
        using var env = LoadedProcessEnv.Create(deterministicAslrSeed: 0x5555_6666_7777_8888);
        var firstLayout = env.Process.Mem.Layout!.Clone();

        var (loc, guestPath) = env.Syscalls.ResolvePath("/hello_static", true);
        Assert.True(loc.IsValid);
        Assert.NotNull(loc.Dentry);
        Assert.NotNull(loc.Mount);

        env.Process.Exec(loc.Dentry!, guestPath, [guestPath], Array.Empty<string>(), loc.Mount!);

        var secondLayout = env.Process.Mem.Layout!;
        Assert.NotEqual(firstLayout.InitialStackTop, secondLayout.InitialStackTop);
        Assert.NotEqual(firstLayout.AuxRandomBytes, secondLayout.AuxRandomBytes);
        Assert.Equal(secondLayout.VdsoBaseHint, env.Process.Syscalls.SigReturnAddr);
    }

    [Fact]
    public async Task ForkThenExec_ReplacesInheritedLayoutWithFreshExecLayout()
    {
        using var env = LoadedProcessEnv.Create(deterministicAslrSeed: 0x9999_aaaa_bbbb_cccc);
        var parentLayout = env.Process.Mem.Layout!.Clone();
        var (loc, guestPath) = env.Syscalls.ResolvePath("/hello_static", true);
        Assert.True(loc.IsValid);
        Assert.NotNull(loc.Dentry);
        Assert.NotNull(loc.Mount);

        var child = await env.Task.Clone(0, 0, 0, 0, 0);
        try
        {
            var inheritedLayout = child.Process.Mem.Layout!;
            Assert.Equal(parentLayout.InitialStackTop, inheritedLayout.InitialStackTop);
            Assert.Equal(parentLayout.MmapBase, inheritedLayout.MmapBase);
            Assert.Equal(parentLayout.AuxRandomBytes, inheritedLayout.AuxRandomBytes);

            child.Process.Syscalls.CurrentSyscallEngine = child.CPU;
            child.Process.Exec(loc.Dentry!, guestPath, [guestPath], Array.Empty<string>(), loc.Mount!);

            var execLayout = child.Process.Mem.Layout!;
            Assert.NotEqual(parentLayout.InitialStackTop, execLayout.InitialStackTop);
            Assert.NotEqual(parentLayout.AuxRandomBytes, execLayout.AuxRandomBytes);
            Assert.Equal(execLayout.VdsoBaseHint, child.Process.Syscalls.SigReturnAddr);
        }
        finally
        {
            child.Process.Syscalls.Close();
            child.CPU.Dispose();
        }
    }

    private static GuestAddressSpaceLayout CreateLayoutWithTaskSize(uint taskSize)
    {
        var baseLayout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray());
        var stackTopMax = taskSize - LinuxConstants.PageSize * 2;
        var initialStackTop = stackTopMax - LinuxConstants.PageSize * 4;
        var mmapBase = taskSize - 0x02000000;
        return new GuestAddressSpaceLayout
        {
            TaskSize = taskSize,
            StackTopMax = stackTopMax,
            InitialStackTop = initialStackTop,
            StackLowerBound = initialStackTop - 0x00100000,
            MmapBase = mmapBase,
            LegacyMmapBase = Math.Min(baseLayout.LegacyMmapBase, mmapBase - LinuxConstants.PageSize),
            PieBase = Math.Min(baseLayout.PieBase, mmapBase - 0x00400000),
            InterpreterBaseHint = Math.Min(baseLayout.InterpreterBaseHint, mmapBase - 0x00200000),
            VdsoBaseHint = taskSize - LinuxConstants.PageSize,
            StackRandomOffset = 0,
            MmapRandomOffset = 0,
            StackGuardGap = baseLayout.StackGuardGap,
            AuxRandomBytes = baseLayout.AuxRandomBytes.ToArray()
        };
    }

    private static void MountTmpfsRoot(KernelRuntime runtime, string sourceName)
    {
        var tmpfsType = new Fiberish.VFS.FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Fiberish.VFS.Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Fiberish.VFS.Tmpfs(memoryContext: memoryContext)
        };
        var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(tmpfsType, 0, sourceName, null);
        runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
        {
            Source = sourceName,
            FsType = "tmpfs",
            Options = "rw"
        });
    }

    private static byte[] BuildDynamicPieElf(string interpreterPath)
    {
        var interpBytes = Encoding.ASCII.GetBytes(interpreterPath + "\0");
        const uint interpOffset = 0x200;
        const uint phoff = 0x34;
        const ushort phnum = 2;
        var imageLength = (int)Math.Max(DynamicLoadOffset + DynamicCodeSize, interpOffset + interpBytes.Length);
        var image = new byte[imageLength];
        var span = image.AsSpan();

        WriteElfHeader(span, ElfFileType.Dynamic, DynamicMainEntryOffset, phoff, phnum);
        WriteProgramHeader(span.Slice((int)phoff, 32), ElfSegmentType.Interpreter, interpOffset, 0, (uint)interpBytes.Length,
            (uint)interpBytes.Length, 0, 1);
        WriteProgramHeader(span.Slice((int)phoff + 32, 32), ElfSegmentType.Load, DynamicLoadOffset, 0, DynamicCodeSize,
            DynamicCodeSize, ElfSegmentFlags.Readable | ElfSegmentFlags.Executable, LinuxConstants.PageSize);

        interpBytes.CopyTo(span[(int)interpOffset..]);
        span.Slice((int)DynamicLoadOffset, (int)DynamicCodeSize).Fill(0x90);
        return image;
    }

    private static byte[] BuildDynamicInterpreterElf()
    {
        const uint phoff = 0x34;
        const ushort phnum = 1;
        var image = new byte[(int)(DynamicLoadOffset + DynamicCodeSize)];
        var span = image.AsSpan();

        WriteElfHeader(span, ElfFileType.Dynamic, DynamicInterpreterEntryOffset, phoff, phnum);
        WriteProgramHeader(span.Slice((int)phoff, 32), ElfSegmentType.Load, DynamicLoadOffset, 0, DynamicCodeSize,
            DynamicCodeSize, ElfSegmentFlags.Readable | ElfSegmentFlags.Executable, LinuxConstants.PageSize);
        span.Slice((int)DynamicLoadOffset, (int)DynamicCodeSize).Fill(0xCC);
        return image;
    }

    private static void WriteElfHeader(Span<byte> span, ElfFileType fileType, uint entryPoint, uint programHeaderOffset,
        ushort programHeaderCount)
    {
        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 1;
        span[5] = 1;
        span[6] = 1;

        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], (ushort)fileType);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], entryPoint);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], programHeaderOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[40..42], 52);
        BinaryPrimitives.WriteUInt16LittleEndian(span[42..44], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(span[44..46], programHeaderCount);
    }

    private static void WriteProgramHeader(Span<byte> header, ElfSegmentType type, uint position, uint virtualAddress,
        uint size, uint sizeInMemory, uint flags, uint align)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], position);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], sizeInMemory);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], align);
    }

    private sealed class LoadedProcessEnv : IDisposable
    {
        private LoadedProcessEnv(TestRuntimeFactory runtime, Engine engine, SyscallManager syscalls, Process process,
            KernelScheduler scheduler, FiberTask task)
        {
            Runtime = runtime;
            Engine = engine;
            Syscalls = syscalls;
            Process = process;
            Scheduler = scheduler;
            Task = task;
        }

        public TestRuntimeFactory Runtime { get; }
        public Engine Engine { get; }
        public SyscallManager Syscalls { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            Syscalls.Close();
            Engine.Dispose();
        }

        public static LoadedProcessEnv Create(ResourceLimit? stackLimit = null, ulong? deterministicAslrSeed = null,
            MemoryRuntimeContext? memoryContext = null)
        {
            memoryContext ??= deterministicAslrSeed.HasValue
                ? TestRuntimeFactory.CreateDeterministicMemoryContext(deterministicAslrSeed.Value)
                : new MemoryRuntimeContext();
            if (deterministicAslrSeed.HasValue && memoryContext.DeterministicAslrSeed != deterministicAslrSeed.Value)
                memoryContext.DeterministicAslrSeed = deterministicAslrSeed.Value;

            var runtime = new TestRuntimeFactory(memoryContext);
            var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            var syscalls = new SyscallManager(engine, mm, 0);
            syscalls.MountRootHostfs(ResolveGuestRootForHelloStatic());

            var scheduler = new KernelScheduler();
            var process = new Process(9301, mm, syscalls);
            if (stackLimit.HasValue)
                process.ResourceLimits[LinuxConstants.RLIMIT_STACK] = stackLimit.Value;

            scheduler.RegisterProcess(process);
            var task = new FiberTask(process.TGID, process, engine, scheduler);
            engine.Owner = task;

            var (loc, guestPath) = syscalls.ResolvePath("/hello_static", true);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry);
            Assert.NotNull(loc.Mount);

            process.LoadExecutable(loc.Dentry!, guestPath, ["/hello_static"], Array.Empty<string>(), loc.Mount!);
            return new LoadedProcessEnv(runtime, engine, syscalls, process, scheduler, task);
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
    }
}
