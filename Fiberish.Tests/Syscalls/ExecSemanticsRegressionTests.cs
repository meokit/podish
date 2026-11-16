using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

[Collection("ExternalPageManagerSerial")]
public class ExecSemanticsRegressionTests
{
    private const uint PathAddr = 0x61000000;
    private const uint ArgvAddr = 0x61001000;
    private const uint EnvpAddr = 0x61002000;
    private const uint StrAddr = 0x61010000;
    private const uint BadPtr = 0xDEAD0000;

    [Fact]
    public async Task SysExecve_MalformedElf_ReturnsEnoexecAndKeepsImage()
    {
        using var env = ExecEnv.CreateTmpfs(9501);
        env.WriteFile("/badelf", MalformedElf(), 0x1ED);
        env.WriteString(PathAddr, "/badelf");

        var oldMem = env.Process.Mem;
        var oldSysMem = env.Syscalls.Mem;
        var oldExe = env.Process.ExecutablePathRaw;
        var rc = await Call(env.Syscalls, "SysExecve", PathAddr);

        Assert.Equal(-(int)Errno.ENOEXEC, rc);
        Assert.Same(oldMem, env.Process.Mem);
        Assert.Same(oldSysMem, env.Syscalls.Mem);
        Assert.Equal(oldExe, env.Process.ExecutablePathRaw);
    }

    [Fact]
    public async Task SysExecve_MalformedElf_PreservesFdSignalAltStackAndRobustList()
    {
        using var env = ExecEnv.CreateTmpfs(9502);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteFile("/badelf", MalformedElf(), 0x1ED);
        env.WriteString(PathAddr, "/badelf");

        var loc = env.Syscalls.ResolvePath("/hello_static").loc;
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount);
        var fd = env.Syscalls.AllocFD(file, closeOnExec: true, task: env.Task);
        Assert.True(fd >= 0);

        env.Task.AltStackSp = 0x70000000;
        env.Task.AltStackSize = 8192;
        env.Task.AltStackFlags = 0;
        env.Task.RobustListHead = 0x70001000;
        env.Task.RobustListSize = 16;
        env.Process.ParentDeathSignal = 9;
        env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x12345678 };

        var rc = await Call(env.Syscalls, "SysExecve", PathAddr);

        Assert.Equal(-(int)Errno.ENOEXEC, rc);
        Assert.True(env.Syscalls.FDs.ContainsKey(fd));
        Assert.True(env.Syscalls.IsFdCloseOnExec(fd));
        Assert.True(env.Process.SignalActions.ContainsKey((int)Signal.SIGUSR1));
        Assert.Equal(0x70000000u, env.Task.AltStackSp);
        Assert.Equal(8192u, env.Task.AltStackSize);
        Assert.Equal(0, env.Task.AltStackFlags);
        Assert.Equal(0x70001000u, env.Task.RobustListHead);
        Assert.Equal(16u, env.Task.RobustListSize);
        Assert.Equal(9, env.Process.ParentDeathSignal);
    }

    [Fact]
    public async Task SysExecve_UnreadableArgvVector_ReturnsEfault()
    {
        using var env = ExecEnv.CreateTmpfs(9503);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");

        Assert.Equal(-(int)Errno.EFAULT,
            await Call(env.Syscalls, "SysExecve", PathAddr, BadPtr, 0));
    }

    [Fact]
    public async Task SysExecve_UnreadableArgvString_ReturnsEfault()
    {
        using var env = ExecEnv.CreateTmpfs(9504);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.WriteVector(ArgvAddr, PathAddr, "/hello_static", null, null);
        env.WriteU32(ArgvAddr + 4, BadPtr);

        Assert.Equal(-(int)Errno.EFAULT,
            await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, 0));
    }

    [Fact]
    public async Task SysExecve_UnreadableEnvpString_ReturnsEfault()
    {
        using var env = ExecEnv.CreateTmpfs(9505);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.WriteVector(ArgvAddr, PathAddr, "/hello_static", null);
        env.WriteVector(EnvpAddr, StrAddr + 0x2000, "VALID=1", null);
        env.WriteU32(EnvpAddr + 4, BadPtr);

        Assert.Equal(-(int)Errno.EFAULT,
            await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, EnvpAddr));
    }

    [Fact]
    public async Task SysExecve_OversizeEnvironment_ReturnsE2big()
    {
        using var env = ExecEnv.CreateTmpfs(9506);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.WriteVector(ArgvAddr, PathAddr, "/hello_static", null);

        const int stringLen = 200 * 1024;
        env.MapRange(EnvpAddr, 0x1000);
        var big = new byte[stringLen + 1];
        big[0] = (byte)'X';
        big[1] = (byte)'=';
        Array.Fill(big, (byte)'A', 2, stringLen - 2);
        env.MapRange(0x62000000, (uint)big.Length);
        env.WriteBytes(0x62000000, big);
        env.WriteU32(EnvpAddr, 0x62000000);
        env.WriteU32(EnvpAddr + 4, 0);

        Assert.Equal(-(int)Errno.E2BIG,
            await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, EnvpAddr));
    }

    [Fact]
    public async Task SysExecve_AuxvUsesRealAndEffectiveCredentials()
    {
        using var env = ExecEnv.CreateTmpfs(9507);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.Process.UID = 1000;
        env.Process.EUID = 1000;
        env.Process.GID = 1000;
        env.Process.EGID = 1000;

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));

        var auxv = ReadAuxv(env.Engine, env.Engine.RegRead(Reg.ESP));
        Assert.Equal(1000u, auxv[LinuxConstants.AT_UID]);
        Assert.Equal(1000u, auxv[LinuxConstants.AT_EUID]);
        Assert.Equal(1000u, auxv[LinuxConstants.AT_GID]);
        Assert.Equal(1000u, auxv[LinuxConstants.AT_EGID]);
        Assert.Equal(0u, auxv[LinuxConstants.AT_SECURE]);
    }

    [Fact]
    public async Task SysExecve_SetIdBinary_AuxvReportsNewEffectiveIdsAndSecure()
    {
        using var env = ExecEnv.CreateTmpfs(9508);
        env.WriteFile("/setuid", HelloBytes(), 0x9ED); // 04755
        var loc = env.Syscalls.ResolvePath("/setuid").loc;
        Assert.True(loc.IsValid);
        loc.Dentry!.Inode!.Uid = 0;
        loc.Dentry.Inode.Gid = 0;
        env.WriteString(PathAddr, "/setuid");
        env.Process.UID = 1000;
        env.Process.EUID = 1000;
        env.Process.GID = 1000;
        env.Process.EGID = 1000;

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));

        var auxv = ReadAuxv(env.Engine, env.Engine.RegRead(Reg.ESP));
        Assert.Equal(1000u, auxv[LinuxConstants.AT_UID]);
        Assert.Equal(0u, auxv[LinuxConstants.AT_EUID]);
        Assert.Equal(1000u, auxv[LinuxConstants.AT_GID]);
        Assert.Equal(1000u, auxv[LinuxConstants.AT_EGID]);
        Assert.Equal(1u, auxv[LinuxConstants.AT_SECURE]);
    }

    [Fact]
    public async Task SysExecve_ShebangAtexecfnKeepsScriptPath()
    {
        using var env = ExecEnv.CreateTmpfs(9509);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteFile("/script", Encoding.UTF8.GetBytes("#!/hello_static\n"), 0x1ED);
        env.WriteString(PathAddr, "/script");
        env.WriteVector(ArgvAddr, PathAddr, "/script", null);

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, 0));

        var auxv = ReadAuxv(env.Engine, env.Engine.RegRead(Reg.ESP));
        Assert.Equal("/script", ReadCString(env.Engine, auxv[LinuxConstants.AT_EXECFN]));
    }

    [Fact]
    public async Task SysExecve_LongShebangScriptPath_PreservesAtexecfnAndArgv()
    {
        using var env = ExecEnv.CreateTmpfs(9517);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        var scriptPath = "/" + new string('s', 100) + ".sh";
        env.WriteFile(scriptPath, Encoding.UTF8.GetBytes("#!/hello_static\n"), 0x1ED);
        env.WriteString(PathAddr, scriptPath);
        env.WriteVector(ArgvAddr, StrAddr, scriptPath, null);

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, 0));

        var sp = env.Engine.RegRead(Reg.ESP);
        var auxv = ReadAuxv(env.Engine, sp);
        Assert.Equal(scriptPath, ReadCString(env.Engine, auxv[LinuxConstants.AT_EXECFN]));
        Assert.Equal(2u, ReadU32(env.Engine, sp));
        Assert.Equal(scriptPath, ReadCString(env.Engine, ReadU32(env.Engine, sp + 8)));
    }

    [Fact]
    public async Task SysExecve_ShebangCommUsesScriptBasename()
    {
        using var env = ExecEnv.CreateTmpfs(9518);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        const string scriptPath = "/this_is_a_long_shebang_name.sh";
        env.WriteFile(scriptPath, Encoding.UTF8.GetBytes("#!/hello_static\n"), 0x1ED);
        env.WriteString(PathAddr, scriptPath);

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Equal("this_is_a_long_", env.Process.Comm);
    }

    [Fact]
    public async Task SysExecve_SuccessResetsSignalAltStackAndRobustState()
    {
        using var env = ExecEnv.CreateTmpfs(9519);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.Task.AltStackSp = 0x70000000;
        env.Task.AltStackSize = 8192;
        env.Task.AltStackFlags = 0;
        env.Task.RobustListHead = 0xDEAD0000;
        env.Task.RobustListSize = 16;
        env.Task.ChildClearTidPtr = 0x6100F000;
        env.Process.ParentDeathSignal = 9;
        env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x12345678 };

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));

        Assert.Equal(0u, env.Task.AltStackSp);
        Assert.Equal(0u, env.Task.AltStackSize);
        Assert.Equal(LinuxConstants.SS_DISABLE, env.Task.AltStackFlags);
        Assert.Equal(0u, env.Task.RobustListHead);
        Assert.Equal(0u, env.Task.RobustListSize);
        Assert.Equal(0u, env.Task.ChildClearTidPtr);
        Assert.Equal(0, env.Process.ParentDeathSignal);
        Assert.False(env.Process.SignalActions.ContainsKey((int)Signal.SIGUSR1));
    }

    [Fact]
    public async Task SysExecve_UnreadableArgvWithMissingPath_ReturnsEfault()
    {
        using var env = ExecEnv.CreateTmpfs(9520);
        env.WriteString(PathAddr, "/definitely_missing");

        Assert.Equal(-(int)Errno.EFAULT,
            await Call(env.Syscalls, "SysExecve", PathAddr, BadPtr, 0));
    }

    [Fact]
    public async Task SysExecve_ClearsGsAndFsBases()
    {
        using var env = ExecEnv.CreateTmpfs(9510);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.Engine.SetSegBase(Seg.GS, 0x12345000);
        env.Engine.SetSegBase(Seg.FS, 0x23456000);

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));

        Assert.Equal(0u, env.Engine.GetSegBase(Seg.GS));
        Assert.Equal(0u, env.Engine.GetSegBase(Seg.FS));
    }

    [Fact]
    public async Task SysExecve_CommUsesExecutableBasenameNotArgv0()
    {
        using var env = ExecEnv.CreateTmpfs(9511);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.WriteVector(ArgvAddr, StrAddr, "foo", null);
        env.Task.ChildClearTidPtr = 0x6100F000;

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr, ArgvAddr, 0));
        Assert.Equal("hello_static", env.Process.Comm);
        Assert.Equal(0u, env.Task.ChildClearTidPtr);
    }

    [Fact]
    public async Task SysExecve_Fifo_ReturnsEacces()
    {
        using var env = ExecEnv.CreateTmpfs(9512);
        env.WriteString(PathAddr, "/fifo");
        Assert.Equal(0, await Call(env.Syscalls, "SysMknodat", LinuxConstants.AT_FDCWD, PathAddr,
            0x1000 | 0x1A4, 0));

        Assert.Equal(-(int)Errno.EACCES,
            await Call(env.Syscalls, "SysExecve", PathAddr));
    }

    [Fact]
    public async Task SysExecve_WritableOpenFile_ReturnsEtxtbsy()
    {
        using var env = ExecEnv.CreateTmpfs(9513);
        env.WriteFile("/busy", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/busy");

        var fd = await Call(env.Syscalls, "SysOpen", PathAddr, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        Assert.Equal(-(int)Errno.ETXTBSY,
            await Call(env.Syscalls, "SysExecve", PathAddr));
    }

    [Fact]
    public async Task SysExecve_NoExecMount_ReturnsEacces()
    {
        using var env = ExecEnv.CreateTmpfs(9514, mountFlags: LinuxConstants.MS_NOEXEC);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");

        Assert.Equal(-(int)Errno.EACCES,
            await Call(env.Syscalls, "SysExecve", PathAddr));
    }

    [Fact]
    public async Task SysExecve_TerminatesSiblingThreads()
    {
        using var env = ExecEnv.CreateTmpfs(9515);
        env.WriteFile("/hello_static", HelloBytes(), 0x1ED);
        env.WriteString(PathAddr, "/hello_static");
        env.MapRange(0x70000000, 0x2000);

        using var siblingEngine = env.Runtime.CreateEngine();
        var sibling = new FiberTask(env.Process.TGID + 1, env.Process, siblingEngine, env.Scheduler)
        {
            ChildClearTidPtr = 0x70000000,
            RobustListHead = 0x70001000
        };
        Assert.Equal(2, env.Process.Threads.Count);

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));

        Assert.Single(env.Process.Threads);
        Assert.True(sibling.Exited);
        Assert.Equal(FiberTaskStatus.Terminated, sibling.Status);
        Assert.Equal(0u, sibling.ChildClearTidPtr);
        Assert.Equal(0u, sibling.RobustListHead);
    }

    [Fact]
    public async Task SysExecve_GuestEntryFlagsAreReset()
    {
        using var env = ExecEnv.CreateTmpfs(9516);
        env.WriteFile("/flagsprobe", BuildFlagsProbeElf(), 0x1ED);
        env.WriteString(PathAddr, "/flagsprobe");
        env.WriteVector(ArgvAddr, StrAddr, "/flagsprobe", null);

        const uint launcherAddr = 0x09000000;
        env.MapExecutablePage(launcherAddr);
        var launcher = new byte[]
        {
            0xF9, // stc
            0xB8, 0x0B, 0x00, 0x00, 0x00, // mov eax, SYS_execve
            0xBB, 0x00, 0x00, 0x00, 0x61, // mov ebx, PathAddr
            0xB9, 0x00, 0x10, 0x00, 0x61, // mov ecx, ArgvAddr
            0x31, 0xD2, // xor edx, edx (envp = NULL)
            0xCD, 0x80 // int 0x80
        };
        env.WriteBytes(launcherAddr, launcher);

        const uint stackTop = 0x70010000;
        env.MapRange(0x70000000, 0x10000);
        env.Engine.Eip = launcherAddr;
        env.Engine.RegWrite(Reg.ESP, stackTop);
        env.Engine.Eflags = 0x202;
        env.Engine.Run(0, 100000);
        if (env.Engine.Status == EmuStatus.Yield)
            env.Engine.Run(0, 100000);

        Assert.True(env.Process.HasExeced, "execve did not complete");
        Span<byte> flags = stackalloc byte[4];
        Assert.True(env.Engine.CopyFromUser(0x08049000, flags));
        var flagsValue = BinaryPrimitives.ReadUInt32LittleEndian(flags);
        Assert.True(flagsValue == 0x202u,
            $"eip=0x{env.Engine.Eip:x8} status={env.Engine.Status} fault={env.Engine.FaultVector} flags=0x{flagsValue:x8}");
    }

    private static byte[] BuildFlagsProbeElf()
    {
        var code = new byte[]
        {
            0x9C, // pushf
            0x8F, 0x05, 0x00, 0x90, 0x04, 0x08, // pop dword ptr [0x08049000]
            0xEB, 0xFE // jmp $
        };
        var image = new byte[0x1000 + code.Length];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 1;
        image[5] = 1;
        image[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16, 2), 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18, 2), 3); // EM_386
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24, 4), 0x08048000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28, 4), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(40, 2), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(42, 2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(44, 2), 2);

        WriteProgramHeader(image.AsSpan(52, 32), 1, 0x1000, 0x08048000, (uint)code.Length, (uint)code.Length, 5);
        WriteProgramHeader(image.AsSpan(84, 32), 1, 0x2000, 0x08049000, 0, 0x1000, 6);
        code.CopyTo(image.AsSpan(0x1000));
        return image;
    }

    private static void WriteProgramHeader(Span<byte> ph, uint type, uint offset, uint vaddr, uint fileSize,
        uint memSize, uint flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(ph[..4], type);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..8], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[8..12], vaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[12..16], vaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[16..20], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[20..24], memSize);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[24..28], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[28..32], 0x1000);
    }

    private static byte[] MalformedElf()
    {
        var bytes = new byte[32];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 1;
        bytes[5] = 1;
        bytes[6] = 1;
        return bytes;
    }

    private static byte[] HelloBytes()
    {
        return File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));
    }

    private static string ResolveGuestRootForHelloStatic()
    {
        const string rel = "tests/linux/hello_static";
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, rel);
            if (File.Exists(candidate))
                return Path.Combine(current.FullName, "tests/linux");
            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/linux/hello_static from test working directory.");
    }

    private static Dictionary<uint, uint> ReadAuxv(Engine engine, uint sp)
    {
        var argc = ReadU32(engine, sp);
        var cursor = sp + 4;
        cursor += (argc + 1) * 4;
        while (ReadU32(engine, cursor) != 0)
            cursor += 4;
        cursor += 4;

        var result = new Dictionary<uint, uint>();
        while (true)
        {
            var type = ReadU32(engine, cursor);
            var value = ReadU32(engine, cursor + 4);
            cursor += 8;
            if (type == 0) break;
            result[type] = value;
        }

        return result;
    }

    private static uint ReadU32(Engine engine, uint addr)
    {
        Span<byte> value = stackalloc byte[4];
        Assert.True(engine.CopyFromUser(addr, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private static string ReadCString(Engine engine, uint addr)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < 4096; i++)
        {
            Span<byte> value = stackalloc byte[1];
            Assert.True(engine.CopyFromUser(addr + (uint)i, value));
            if (value[0] == 0) break;
            bytes.Add(value[0]);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
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

    private sealed class ExecEnv : IDisposable
    {
        private ExecEnv(TestRuntimeFactory runtime, Engine engine, VMAManager memory, SyscallManager syscalls,
            KernelScheduler scheduler, Process process, FiberTask task, Mount rootMount)
        {
            Runtime = runtime;
            Engine = engine;
            Memory = memory;
            Syscalls = syscalls;
            Scheduler = scheduler;
            Process = process;
            Task = task;
            RootMount = rootMount;
        }

        public TestRuntimeFactory Runtime { get; }
        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager Syscalls { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public Mount RootMount { get; }

        public static ExecEnv CreateTmpfs(int pid, uint mountFlags = 0)
        {
            var runtime = new TestRuntimeFactory();
            var engine = runtime.CreateEngine();
            var memory = runtime.CreateAddressSpace();
            var syscalls = new SyscallManager(engine, memory, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, $"exec-semantics-{pid}", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw",
                Flags = mountFlags
            };
            syscalls.InitializeRoot(sb.Root, mount);
            syscalls.MountStandardProc();

            var scheduler = new KernelScheduler();
            var process = new Process(pid, memory, syscalls);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(process.TGID, process, engine, scheduler);
            engine.Owner = task;
            engine.CurrentSyscallManager = syscalls;

            return new ExecEnv(runtime, engine, memory, syscalls, scheduler, process, task, mount);
        }

        public void WriteFile(string path, byte[] content, int mode)
        {
            Syscalls.WriteGuestFile(path, content, mode);
        }

        public void WriteString(uint addr, string value)
        {
            MapRange(addr, LinuxConstants.PageSize);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public void WriteBytes(uint addr, byte[] bytes)
        {
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public void WriteU32(uint addr, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public void WriteVector(uint vectorAddr, uint stringBase, params string?[] strings)
        {
            MapRange(vectorAddr, LinuxConstants.PageSize);
            var stringAddr = stringBase;
            for (var i = 0; i < strings.Length; i++)
            {
                var value = strings[i];
                if (value == null)
                {
                    WriteU32(vectorAddr + (uint)(i * 4), 0);
                    continue;
                }

                WriteU32(vectorAddr + (uint)(i * 4), stringAddr);
                var bytes = Encoding.UTF8.GetBytes(value + "\0");
                MapRange(stringAddr, (uint)bytes.Length);
                Assert.True(Engine.CopyToUser(stringAddr, bytes));
                stringAddr += (uint)((bytes.Length + 15) & ~15);
            }

            WriteU32(vectorAddr + (uint)(strings.Length * 4), 0);
        }

        public void MapRange(uint addr, uint length)
        {
            var start = addr & LinuxConstants.PageMask;
            var end = (addr + length + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
            for (var page = start; page < end; page += LinuxConstants.PageSize)
                MapUserPage(Memory, Engine, page);
        }

        public void MapExecutablePage(uint addr)
        {
            Memory.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write | Protection.Exec,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test-code]", Engine);
            Assert.True(Memory.HandleFault(addr, true, Engine));
        }

        public void Dispose()
        {
            Syscalls.Close();
            Engine.Dispose();
        }
    }

    private static void MapUserPage(VMAManager mm, Engine engine, uint addr)
    {
        if (mm.FindVmArea(addr) != null) return;
        mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", engine);
        Assert.True(mm.HandleFault(addr, true, engine));
    }
}
