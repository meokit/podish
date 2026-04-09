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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;

        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);
        var guestRoot = ResolveGuestRootForHelloStatic();
        sm.MountRootHostfs(guestRoot);
        sm.MountStandardProc();

        var scheduler = new KernelScheduler();
        var process = new Process(9201, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        try
        {
            const uint filenameAddr = 0x61000000;
            MapUserPage(mm, engine, filenameAddr);
            WriteCString(engine, filenameAddr, "/hello_static");

            // Keep one page pinned so exec's first strict allocation reliably trips the quota
            // even if the old address space releases pages before loading the new image.
            var reservedPage = ExternalPageManager.AllocateExternalPage();
            try
            {
                ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize;

                var rc = await Call(sm, "SysExecve", filenameAddr);
                Assert.Equal(-(int)Errno.ENOMEM, rc);
            }
            finally
            {
                ExternalPageManager.ReleasePtr(reservedPage);
            }
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
            sm.Close();
        }
    }

    [Fact]
    public async Task SysExecve_WhenProcSelfFdPointsToUnlinkedMemfdScript_UsesLiveFileForShebang()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        using var engine = new Engine();
        var mm = new VMAManager();
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

            var script = Encoding.UTF8.GetBytes("#!/hello_static\n");
            var writeRc = file!.OpenedInode!.WriteFromHost(task, file, script, 0);
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
    public async Task SysExecve_WhenOverlayUpperExecutableClearsAddressSpace_ReleasesTransientExecFileHolders()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        using var engine = new Engine();
        var mm = new VMAManager();
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
            var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "execve-upper-lower", null);
            lowerSb.MemoryContext = engine.MemoryContext;
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var helloBytes = File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));
            sm.WriteFileInDetachedMount(sm.RootMount!, "hello_static", helloBytes, 0x1ED);

            WriteCString(engine, pathAddr, "/hello_static");
            var execRc = await Call(sm, "SysExecve", pathAddr);
            Assert.Equal(0, execRc);
            Assert.NotSame(mm, process.Mem);

            var (execLoc, _) = sm.ResolvePath("/hello_static");
            var overlayInode = Assert.IsType<OverlayInode>(execLoc.Dentry!.Inode);
            var upperInode = Assert.IsAssignableFrom<IndexedMemoryInode>(overlayInode.UpperInode);
            var holdersAfterExec = upperInode.DescribeActiveFileHoldersForDebug();

            Assert.DoesNotContain("SysExecve", holdersAfterExec, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveExecutableImage", holdersAfterExec, StringComparison.Ordinal);

            mm.Clear(engine);

            var holdersAfterClear = upperInode.DescribeActiveFileHoldersForDebug();
            Assert.DoesNotContain("SysExecve", holdersAfterClear, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveExecutableImage", holdersAfterClear, StringComparison.Ordinal);
            Assert.Contains("LoadSegments", holdersAfterClear, StringComparison.Ordinal);

            process.Mem.Clear(engine);

            Assert.Equal("<none>", upperInode.DescribeActiveFileHoldersForDebug());
        }
        finally
        {
            sm.Close();
            if (Directory.Exists(silkRoot))
                Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public async Task ProcFdReadlink_WhenMemfd_DoesNotAppendDeletedSuffix()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        using var engine = new Engine();
        var mm = new VMAManager();
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
    public async Task ProcFdOpen_CreatesNewFileDescriptionWithIndependentOffset()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        using var engine = new Engine();
        var mm = new VMAManager();
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

            Assert.Equal(0, await Call(sm, "SysLseek", (uint)fd, 0, 0));
            Assert.Equal(1, await Call(sm, "SysRead", (uint)fd, buf1Addr, 1));
            Assert.Equal("a", Encoding.UTF8.GetString(ReadBytes(engine, buf1Addr, 1)));

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var reopenedFd = await Call(sm, "SysOpen", pathAddr, (uint)FileFlags.O_RDONLY);
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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        using var engine = new Engine();
        var mm = new VMAManager();
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
            Assert.Equal(0, await Call(sm, "SysLseek", (uint)fd, 0, 0));

            process.UID = process.EUID = process.SUID = process.FSUID = 1000;
            process.GID = process.EGID = process.SGID = process.FSGID = 1000;

            Assert.Equal(1, await Call(sm, "SysRead", (uint)fd, bufAddr, 1));
            Assert.Equal("z", Encoding.UTF8.GetString(ReadBytes(engine, bufAddr, 1)));

            WriteCString(engine, pathAddr, $"/proc/{process.TGID}/fd/{fd}");
            var reopenedFd = await Call(sm, "SysOpen", pathAddr, (uint)FileFlags.O_RDONLY);
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
}
