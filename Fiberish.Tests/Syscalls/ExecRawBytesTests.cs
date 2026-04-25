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
public class ExecRawBytesTests
{
    [Fact]
    public async Task SysExecve_InvalidUtf8Filename_ExecsSuccessfully()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);

        // Use tmpfs (not hostfs) so invalid UTF-8 filenames are fully supported
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpfsType, 0, "exec-raw-test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9301, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        try
        {
            // Create invalid UTF-8 filename: 0xC0 0x80 is an overlong encoding of NUL
            var invalidUtf8Name = new byte[] { 0xC0, 0x80, (byte)'x' };
            var helloBytes = File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));

            var parent = rootSb.Root;
            var fileDentry = new Dentry(FsName.FromBytes(invalidUtf8Name), null, parent, parent.SuperBlock);
            parent.Inode!.Create(fileDentry, 0x1ED, 0, 0);
            var wf = new LinuxFile(fileDentry, FileFlags.O_WRONLY, rootMount);
            fileDentry.Inode!.WriteFromHost(task, wf, helloBytes, 0);
            wf.Close();

            const uint pathAddr = 0x61000000;
            MapUserPage(mm, engine, pathAddr);
            var pathBytes = new byte[] { (byte)'/' }.Concat(invalidUtf8Name).Concat(new byte[] { 0 }).ToArray();
            Assert.True(engine.CopyToUser(pathAddr, pathBytes));

            var rc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(0, rc);
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task SysExecve_InvalidUtf8Argv_PreservedOnStack()
    {
        var runtime = new TestRuntimeFactory(0x1234_5678_9ABC_DEF0);
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(ResolveGuestRootForHelloStatic());
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9302, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        try
        {
            const uint pathAddr = 0x61100000;
            const uint argvAddr = 0x61101000;
            const uint arg0Addr = 0x61102000;
            const uint arg1Addr = 0x61103000;

            MapUserPage(mm, engine, pathAddr);
            MapUserPage(mm, engine, argvAddr);
            MapUserPage(mm, engine, arg0Addr);
            MapUserPage(mm, engine, arg1Addr);

            var invalidUtf8Arg = new byte[] { 0xC0, 0x80, (byte)'x' };
            WriteCString(engine, pathAddr, "/hello_static");

            var arg0Bytes = invalidUtf8Arg.Concat(new byte[] { 0 }).ToArray();
            var arg1Bytes = Encoding.UTF8.GetBytes("second_arg").Concat(new byte[] { 0 }).ToArray();
            Assert.True(engine.CopyToUser(arg0Addr, arg0Bytes));
            Assert.True(engine.CopyToUser(arg1Addr, arg1Bytes));

            var ptrBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(ptrBuf, arg0Addr);
            Assert.True(engine.CopyToUser(argvAddr, ptrBuf));
            BinaryPrimitives.WriteUInt32LittleEndian(ptrBuf, arg1Addr);
            Assert.True(engine.CopyToUser(argvAddr + 4, ptrBuf));
            BinaryPrimitives.WriteUInt32LittleEndian(ptrBuf, 0);
            Assert.True(engine.CopyToUser(argvAddr + 8, ptrBuf));

            Assert.Equal(0, await Call(sm, "SysExecve", pathAddr, argvAddr, 0));

            Assert.Equal(invalidUtf8Arg, process.CommandLineArgumentBytes[0]);
            Assert.Equal("second_arg", process.CommandLineArguments[1]);

            var expectedCmdline = new byte[arg0Bytes.Length + arg1Bytes.Length];
            arg0Bytes.CopyTo(expectedCmdline, 0);
            arg1Bytes.CopyTo(expectedCmdline, arg0Bytes.Length);
            Assert.Equal(expectedCmdline, process.CommandLineRaw);
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public async Task SysExecve_InvalidUtf8ShebangScript_PreservesScriptPath()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);

        // Use tmpfs for invalid UTF-8 support
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpfsType, 0, "shebang-raw-test", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9303, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        try
        {
            var invalidUtf8Name = new byte[] { 0xC0, 0x80, (byte)'s' };
            var helloBytes = File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));

            // Create interpreter on tmpfs
            var interpDentry = new Dentry(FsName.FromString("hello_static"), null, rootSb.Root, rootSb);
            rootSb.Root.Inode!.Create(interpDentry, 0x1ED, 0, 0);
            var interpWf = new LinuxFile(interpDentry, FileFlags.O_WRONLY, rootMount);
            interpDentry.Inode!.WriteFromHost(task, interpWf, helloBytes, 0);
            interpWf.Close();

            // Create script with invalid UTF-8 name on tmpfs
            var scriptDentry = new Dentry(FsName.FromBytes(invalidUtf8Name), null, rootSb.Root, rootSb);
            rootSb.Root.Inode.Create(scriptDentry, 0x1ED, 0, 0);
            var scriptContent = Encoding.UTF8.GetBytes("#!/hello_static\n");
            var scriptWf = new LinuxFile(scriptDentry, FileFlags.O_WRONLY, rootMount);
            scriptDentry.Inode!.WriteFromHost(task, scriptWf, scriptContent, 0);
            scriptWf.Close();

            const uint pathAddr = 0x61000000;
            MapUserPage(mm, engine, pathAddr);
            var pathBytes = new byte[] { (byte)'/' }.Concat(invalidUtf8Name).Concat(new byte[] { 0 }).ToArray();
            Assert.True(engine.CopyToUser(pathAddr, pathBytes));

            var rc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(0, rc);

            Assert.True(process.CommandLineArgumentBytes.Length >= 2);
            Assert.Equal("/hello_static", process.CommandLineArguments[0]);
            var scriptArg = process.CommandLineArgumentBytes[1];
            Assert.Equal(invalidUtf8Name.Length + 1, scriptArg.Length);
            Assert.Equal((byte)'/', scriptArg[0]);
            Assert.Equal(invalidUtf8Name, scriptArg.AsSpan(1).ToArray());
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public void ProcExeAndCmdline_ReturnRawBytes()
    {
        using var ctx = new ProcTestContext();

        var process = TestRuntimeFactory.CreateProcess(9401);
        process.PPID = 1;
        process.PGID = 9401;
        process.SID = 9401;
        process.State = ProcessState.Running;

        var invalidUtf8Path = new byte[] { (byte)'/', 0xC0, 0x80, (byte)'x' };
        var exeProp = typeof(Process).GetProperty(nameof(Process.ExecutablePathRaw),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(exeProp);
        exeProp!.SetValue(process, invalidUtf8Path);

        var argsRaw = new[]
        {
            new byte[] { 0xC0, 0x80, (byte)'a' },
            Encoding.UTF8.GetBytes("valid")
        };
        var cmdlineRaw = new byte[argsRaw[0].Length + 1 + argsRaw[1].Length + 1];
        argsRaw[0].CopyTo(cmdlineRaw, 0);
        cmdlineRaw[argsRaw[0].Length] = 0;
        argsRaw[1].CopyTo(cmdlineRaw, argsRaw[0].Length + 1);
        cmdlineRaw[argsRaw[0].Length + 1 + argsRaw[1].Length] = 0;

        var argsProp = typeof(Process).GetProperty(nameof(Process.CommandLineRaw),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(argsProp);
        argsProp!.SetValue(process, cmdlineRaw);

        ctx.Scheduler.RegisterProcess(process);

        var fs = new ProcFileSystem();
        var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);
        var mount = new Mount(sb, sb.Root)
        {
            Source = "proc",
            FsType = "proc",
            Options = "rw,relatime"
        };

        var pidDir = Lookup(ctx.Task, sb.Root, "9401");
        Assert.NotNull(pidDir);

        var exe = Lookup(ctx.Task, pidDir!, "exe");
        var cmdline = Lookup(ctx.Task, pidDir, "cmdline");
        Assert.NotNull(exe);
        Assert.NotNull(cmdline);

        var exeBytes = ReadlinkBytes(ctx.Task, exe!);
        Assert.Equal(invalidUtf8Path, exeBytes);

        var cmdlineBytes = ReadAllBytes(ctx.Task, cmdline!, mount);
        Assert.Equal(cmdlineRaw, cmdlineBytes);
    }

    private static byte[] ReadlinkBytes(FiberTask task, Dentry dentry)
    {
        if (dentry.Inode is IContextualSymlinkInode contextual)
            return contextual.Readlink(task);
        return [];
    }

    private static byte[] ReadAllBytes(FiberTask task, Dentry dentry, Mount mount)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        try
        {
            BindTaskContext(file, task);
            var result = new List<byte>();
            var buffer = new byte[256];
            long offset = 0;
            while (true)
            {
                var n = dentry.Inode!.ReadToHost(null, file, buffer, offset);
                Assert.True(n >= 0);
                if (n == 0) break;
                result.AddRange(buffer.AsSpan(0, n).ToArray());
                offset += n;
            }
            return [.. result];
        }
        finally
        {
            file.Close();
        }
    }

    private static void MapUserPage(VMAManager mm, Engine engine, uint addr)
    {
        mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", engine);
        Assert.True(mm.HandleFault(addr, true, engine));
    }

    private static void WriteCString(Engine engine, uint addr, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Assert.True(engine.CopyToUser(addr, bytes));
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

    private static void SetUnixMode(string path, int mode)
    {
#pragma warning disable CA1416
        File.SetUnixFileMode(path, (UnixFileMode)mode);
#pragma warning restore CA1416
    }

    private static Dentry? Lookup(FiberTask task, Dentry parent, string name)
    {
        return parent.Inode is IContextualDirectoryInode contextual
            ? contextual.Lookup(task, Encoding.UTF8.GetBytes(name))
            : parent.Inode?.Lookup(name);
    }

    private static void BindTaskContext(LinuxFile file, FiberTask task)
    {
        if (file.OpenedInode is ITaskContextBoundInode taskBoundInode)
            taskBoundInode.BindTaskContext(file, task);
    }

    private sealed class ProcTestContext : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public ProcTestContext()
        {
            Engine = _runtime.CreateEngine();
            Memory = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Memory, 0);
            Scheduler = new KernelScheduler();
            Process = new Process(1, Memory, SyscallManager);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(1, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Scheduler.CurrentTask = Task;
        }

        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            GC.KeepAlive(Task);
        }
    }
}
