using System.Buffers;
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

public class FsFdReviewRegressionTests
{
    private const uint PathPtr = 0x10000;
    private const uint DataPtr = 0x11000;
    private const uint IovPtr = 0x12000;
    private const uint InfoPtr = 0x13000;

    // Review #1: O_PATH bypasses access checks only because it grants no data access.
    [Theory]
    [InlineData(FileFlags.O_PATH)]
    [InlineData(FileFlags.O_PATH | FileFlags.O_RDWR | FileFlags.O_TRUNC | FileFlags.O_CREAT |
                FileFlags.O_EXCL | FileFlags.O_APPEND)]
    public async Task OPath_UnprivilegedOpen_DoesNotGrantReadOrWrite(FileFlags flags)
    {
        using var env = new TestEnv();
        var regular = env.AddFile("secret", "SECRET", 0x180);
        env.BecomeUser();
        env.WriteString(PathPtr, "/secret");
        env.Write(DataPtr, "BAD"u8.ToArray());
        env.WriteIovecs((DataPtr, 3));
        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysOpen", PathPtr, (uint)FileFlags.O_RDONLY));
        var fd = await env.Call("SysOpen", PathPtr, (uint)flags);
        Assert.True(fd >= 0);
        Assert.Equal(FileFlags.O_PATH, env.Sm.GetFD(fd)!.Flags);

        foreach (var method in new[] { "SysRead", "SysWrite", "SysPRead", "SysPWrite" })
            Assert.Equal(-(int)Errno.EBADF, await env.Call(method, (uint)fd, DataPtr, 3));
        foreach (var method in new[] { "SysReadV", "SysWriteV", "SysPReadV", "SysPWriteV" })
            Assert.Equal(-(int)Errno.EBADF, await env.Call(method, (uint)fd, IovPtr, 1));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysSendfile", (uint)regular, (uint)fd, 0, 3));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysCopyFileRange", (uint)fd, 0, (uint)regular, 0, 3));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysFcntl64", (uint)fd, 4, (uint)FileFlags.O_RDWR));
        foreach (var method in new[] { "SysLseek", "SysFtruncate", "SysFtruncate64", "SysFchmod", "SysFchown",
                     "SysFsync", "SysFdatasync", "SysFlock" })
            Assert.Equal(-(int)Errno.EBADF, await env.Call(method, (uint)fd));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysFListXAttr", (uint)fd, DataPtr, 64));
        Assert.Equal(0, await env.Call("SysFstat64", (uint)fd, InfoPtr));
        var duplicate = await env.Call("SysDup", (uint)fd);
        Assert.True(duplicate >= 0);
        Assert.True(env.Sm.GetFD(duplicate)!.IsPathOnly);
        Assert.Equal("SECRET", env.FileContents(regular));
    }

    [Fact]
    public async Task OPath_CloexecIsPreserved_ButNotReportedAsAStatusFlag()
    {
        using var env = new TestEnv();
        env.AddFile("secret", "SECRET");
        env.WriteString(PathPtr, "/secret");
        var fd = await env.Call("SysOpen", PathPtr, (uint)(FileFlags.O_PATH | FileFlags.O_CLOEXEC));
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysFcntl64", (uint)fd, 1));
        Assert.Equal((int)FileFlags.O_PATH, await env.Call("SysFcntl64", (uint)fd, 3));
    }

    [Fact]
    public async Task Fsync_PipeAndSocket_ReturnsEinval()
    {
        using var env = new TestEnv();
        var (_, _, writer) = env.AddPipe();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysFsync", (uint)writer));
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysFdatasync", (uint)writer));

        var socket = await env.Call("SysSocket", LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0u);
        Assert.True(socket >= 0);
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysFsync", (uint)socket));
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysFdatasync", (uint)socket));
    }

    // Review #2: ArrayPool<T>.Rent(n) is not an n-element input vector.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Vmsplice_UsesOnlyTheSubmittedIovecs(int count)
    {
        using var env = new TestEnv();
        var (pipe, reader, writer) = env.AddPipe();
        env.Write(DataPtr, "AX"u8.ToArray());
        env.WriteIovecs(Enumerable.Repeat((DataPtr, 1U), count).ToArray());
        var stale = ArrayPool<Iovec>.Shared.Rent(count);
        Array.Fill(stale, new Iovec { BaseAddr = DataPtr + 1, Len = 1 });
        ArrayPool<Iovec>.Shared.Return(stale);

        Assert.Equal(count, await env.Call("SysVmsplice", (uint)writer, IovPtr, (uint)count, 2));
        var bytes = new byte[64];
        Assert.Equal(count, pipe.ReadToHost(env.Task, env.Sm.GetFD(reader)!, bytes, 0));
        Assert.Equal(new string('A', count), Encoding.ASCII.GetString(bytes, 0, count));
    }

    // Review #3: a partially free pipe must not turn EAGAIN into synchronous spinning.
    [Fact(Timeout = 5000)]
    public async Task Vmsplice_SmallAtomicWrite_YieldsUntilTheWholeChunkFits()
    {
        using var env = new TestEnv();
        var pipe = new AtomicWaitPipe(env.Scheduler);
        var (_, reader, writer) = env.AddPipe(pipe);
        Assert.Equal(4096, pipe.SetPipeSize(4096, true));
        Assert.Equal(4095, pipe.WriteFromHost(env.Task, env.Sm.GetFD(writer)!, new byte[4095], 0));
        env.Write(DataPtr, "AB"u8.ToArray());
        env.WriteIovecs((DataPtr, 2));

        var pending = env.Call("SysVmsplice", (uint)writer, IovPtr, 1).AsTask();
        Assert.Equal(2, pipe.RequestedMinWritableBytes);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, pipe.ReadToHost(env.Task, env.Sm.GetFD(reader)!, new byte[1], 0));
        for (var i = 0; i < 50 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }
        Assert.True(pending.IsCompleted);
        Assert.Equal(2, await pending);
    }

    // Reviews #4/#8: fd tables (including CLOEXEC bits) belong to tasks, not a process.
    [Theory]
    [InlineData(2U)]
    [InlineData(6U)]
    public async Task CloseRange_Unshare_DoesNotChangeSiblingDescriptors(uint flags)
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "data");
        var sibling = await env.CloneThread();
        Assert.Equal(0, await env.CallOn(sibling, "SysCloseRange", (uint)fd, (uint)fd, flags));
        Assert.Equal(flags == 2 ? -(int)Errno.EBADF : 1,
            await env.CallOn(sibling, "SysFcntl64", (uint)fd, 1));
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 1));
        Assert.Equal("data", env.FileContents(fd));

        // Newly cloned threads must inherit the caller's private table, not the leader's.
        env.SwitchTo(sibling);
        var grandchild = await sibling.Clone(ThreadFlags, 0, 0, 0, 0);
        Assert.Equal(flags == 2 ? -(int)Errno.EBADF : 1,
            await env.CallOn(grandchild, "SysFcntl64", (uint)fd, 1));
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 1));
    }

    [Fact]
    public async Task CloseRange_Unshare_PrivateTableIsClosedOnThreadExit()
    {
        using var env = new TestEnv();
        var (pipe, reader, writer) = env.AddPipe();
        var sibling = await env.CloneThread();
        Assert.Equal(0, await env.CallOn(sibling, "SysCloseRange", 1000, 2000, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)writer));
        var readFile = env.Sm.GetFD(reader)!;
        Assert.Equal(-(int)Errno.EAGAIN, pipe.ReadToHost(env.Task, readFile, new byte[1], 0));
        Assert.Equal(0, await env.CallOn(sibling, "SysExit"));
        Assert.Equal(0, pipe.ReadToHost(env.Task, readFile, new byte[1], 0));
    }

    [Fact]
    public async Task CloseRange_Unshare_ForkUsesTheCallingThreadsTable()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "data");
        var sibling = await env.CloneThread();
        Assert.Equal(0, await env.CallOn(sibling, "SysCloseRange", (uint)fd, (uint)fd, 6));
        env.SwitchTo(sibling);
        var fork = await sibling.Clone(17, 0, 0, 0, 0);
        Assert.Equal(1, await env.CallOn(fork, "SysFcntl64", (uint)fd, 1));
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 1));
        fork.Process.Syscalls.Close();
    }

    [Fact]
    public async Task CloseRange_Unshare_DoesNotReleaseLocksOnRetainedDescriptors()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "data");
        var state = env.Sm.GetFD(fd)!.OpenedInode!.GetOrCreateFileLockState();
        Assert.Equal(0, await state.SetLockAsync(env.Process, env.Process.TGID, FileLockState.F_WRLCK,
            0, 9, false, null));
        Assert.Equal(0, await env.Call("SysCloseRange", 1000, 2000, 2));
        Assert.True(state.GetLock(new object(), FileLockState.F_WRLCK, 0, 9).HasConflict);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.False(state.GetLock(new object(), FileLockState.F_WRLCK, 0, 9).HasConflict);
    }

    [Fact]
    public async Task CloneThread_WithoutCloneFiles_CopiesTheCallingThreadsTable()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "data");
        var sibling = await env.Task.Clone(ThreadFlags & ~(int)LinuxConstants.CLONE_FILES, 0, 0, 0, 0);
        Assert.Equal(0, await env.CallOn(sibling, "SysClose", (uint)fd));
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 1));
        var later = env.AddFile("later", "data");
        Assert.Equal(-(int)Errno.EBADF, await env.CallOn(sibling, "SysFcntl64", (uint)later, 1));
    }

    [Fact]
    public async Task FdHelpers_AfterUnshare_ExplicitTaskOverridesAmbientSiblingContext()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "data");
        var sibling = await env.CloneThread();
        Assert.Equal(0, await env.CallOn(sibling, "SysCloseRange", (uint)fd, (uint)fd, 2));
        env.SwitchTo(env.Task);
        var original = env.Sm.GetFD(fd)!;
        var received = new LinuxFile(original.Dentry, FileFlags.O_RDWR, original.Mount);
        // Models an accept/SCM_RIGHTS continuation after another thread ran a syscall.
        var receivedFd = env.Sm.AllocFD(received, minFd: fd + 1, closeOnExec: true, task: sibling);
        Assert.Null(env.Sm.GetFD(receivedFd));
        Assert.Same(received, env.Sm.GetFD(receivedFd, sibling));
        Assert.Equal(1, await env.CallOn(sibling, "SysFcntl64", (uint)receivedFd, 1));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysFcntl64", (uint)receivedFd, 1));
    }

    // Review #6: close(any fd) releases POSIX locks; last close releases OFD locks.
    [Fact]
    public async Task FdAllocation_FailureConsumesOnlyTheOwnedReferenceAndDupPreservesTheOriginal()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("fd-limit", "data");
        var original = env.Sm.GetFD(fd)!;
        var inode = original.OpenedInode!;
        Assert.True(env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE,
            new ResourceLimit((ulong)fd + 1, (ulong)fd + 1)));
        var owned = new LinuxFile(original.Dentry, FileFlags.O_RDWR, original.Mount);
        var finalCloses = 0;
        owned.FinalClosing += () => finalCloses++;
        Assert.Equal(-(int)Errno.EMFILE, env.Sm.AllocFD(owned));
        Assert.Equal(1, finalCloses);
        Assert.Equal(0, typeof(LinuxFile).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned));
        Assert.Equal(-(int)Errno.EMFILE, env.Sm.DupFD(fd));
        Assert.Equal(1, typeof(LinuxFile).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original));
        var before = inode.FileOpenRefCount;
        env.WriteString(PathPtr, "/fd-limit");
        Assert.Equal(-(int)Errno.EMFILE, await env.Call("SysOpen", PathPtr, (uint)FileFlags.O_RDONLY));
        Assert.Equal(before, inode.FileOpenRefCount);
        Assert.Same(original, env.Sm.GetFD(fd));
    }

    [Fact]
    public async Task ScmRights_CopyoutFailureRollsBackExplicitTaskTableNotAmbientSibling()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("rights", "data");
        var original = env.Sm.GetFD(fd)!;
        var sibling = await env.CloneThread();
        Assert.Equal(0, await env.CallOn(sibling, "SysCloseRange", (uint)fd, (uint)fd, 2));
        env.SwitchTo(env.Task);
        original.Get(); // One received SCM_RIGHTS reference; AllocFD owns it, even on failure.
        var writeCmsg = typeof(SyscallManager).GetMethod("WriteCmsg", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = { sibling.CPU, 0xDEAD0000u, 16, new List<LinuxFile> { original }, null, 0, false, true };
        Assert.Equal(-(int)Errno.EFAULT, writeCmsg.Invoke(env.Sm, arguments));
        Assert.Null(env.Sm.GetFD(fd, sibling));
        Assert.Same(original, env.Sm.GetFD(fd));
        Assert.Equal(1, typeof(LinuxFile).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlay_Close_ReleasesTheSameLockStateUsedForAcquisition(bool ofd)
    {
        using var env = new TestEnv(overlay: true);
        env.WriteString(PathPtr, "/lower-file");
        var fd = await env.Call("SysOpen", PathPtr, (uint)FileFlags.O_RDWR);
        var file = env.Sm.GetFD(fd)!;
        var state = file.OpenedInode!.GetOrCreateFileLockState();
        object owner = ofd ? file : env.Process;
        Assert.Equal(0, await state.SetLockAsync(owner, ofd ? -1 : env.Process.TGID,
            FileLockState.F_WRLCK, 0, 9, false, null));
        var duplicate = env.Sm.DupFD(fd);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(ofd, state.GetLock(new object(), FileLockState.F_WRLCK, 0, 9).HasConflict);
        Assert.Equal(0, await env.Call("SysClose", (uint)duplicate));
        Assert.False(state.GetLock(new object(), FileLockState.F_WRLCK, 0, 9).HasConflict);
    }

    // Review #9: copy_file_range must not overwrite an O_APPEND destination.
    [Fact]
    public async Task CopyFileRange_AppendDestination_ReturnsEbadfWithoutChangingFilesOrOffsets()
    {
        using var env = new TestEnv();
        var input = env.AddFile("input", "ABC");
        var output = env.AddFile("output", "old", flags: FileFlags.O_RDWR | FileFlags.O_APPEND);
        Assert.Equal(-(int)Errno.EBADF,
            await env.Call("SysCopyFileRange", (uint)input, 0, (uint)output, 0, 3));
        Assert.Equal("old", env.FileContents(output));
        Assert.Equal(0, env.Sm.GetFD(input)!.Position);
        Assert.Equal(0, env.Sm.GetFD(output)!.Position);
    }

    // Review #10: enforce the actual range, not just the starting file offset.
    [Theory]
    [InlineData("SysWrite", false)]
    [InlineData("SysPWrite", false)]
    [InlineData("SysWriteV", true)]
    [InlineData("SysPWriteV", true)]
    public async Task FsizeLimit_ClipsWritesAcrossTheLimit(string method, bool vector)
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "");
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_FSIZE, new ResourceLimit(4, 4));
        env.Sm.GetFD(fd)!.Position = 1;
        env.Write(DataPtr, "ABCD"u8.ToArray());
        env.WriteIovecs((DataPtr, 2), (DataPtr + 2, 2));
        Assert.Equal(3, await env.Call(method, (uint)fd, vector ? IovPtr : DataPtr,
            vector ? 2U : 4U, 1));
        Assert.Equal("\0ABC", env.FileContents(fd));
        Assert.Equal(0UL, env.Task.GetVisiblePendingSignals() & (1UL << ((int)Signal.SIGXFSZ - 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FsizeLimit_ZeroLengthWriteAtLimit_SucceedsWithoutSignal(bool vector)
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "ABCD");
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_FSIZE, new ResourceLimit(4, 4));
        env.Sm.GetFD(fd)!.Position = 4;
        env.WriteIovecs((DataPtr, 0));
        Assert.Equal(0, await env.Call(vector ? "SysWriteV" : "SysWrite", (uint)fd,
            vector ? IovPtr : DataPtr, vector ? 1U : 0U));
        Assert.Equal(0UL, env.Task.GetVisiblePendingSignals() & (1UL << ((int)Signal.SIGXFSZ - 1)));
    }

    [Fact]
    public async Task FsizeLimit_AppendUsesEof_AndRaisesSigxfszOnlyWhenNoByteFits()
    {
        using var env = new TestEnv();
        var fd = env.AddFile("file", "ABC", flags: FileFlags.O_RDWR | FileFlags.O_APPEND);
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_FSIZE, new ResourceLimit(4, 4));
        env.Write(DataPtr, "DE"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, DataPtr, 2));
        Assert.Equal("ABCD", env.FileContents(fd));
        env.Sm.GetFD(fd)!.Position = 0;
        Assert.Equal(-(int)Errno.EFBIG, await env.Call("SysWrite", (uint)fd, DataPtr, 1));
        Assert.NotEqual(0UL, env.Task.GetVisiblePendingSignals() & (1UL << ((int)Signal.SIGXFSZ - 1)));
        Assert.Equal("ABCD", env.FileContents(fd));
    }

    // Review #11: O_PATH directories are valid for fchdir, subject to search permission.
    [Theory]
    [InlineData(0x1FF, 0)]
    [InlineData(0x180, -(int)Errno.EACCES)]
    public async Task Fchdir_OPathDirectory_ChecksSearchPermission(int mode, int expected)
    {
        using var env = new TestEnv();
        var root = env.Sm.Root.Dentry!;
        var dir = new Dentry(FsName.FromString("dir"), null, root, root.SuperBlock);
        Assert.Equal(0, root.Inode!.Mkdir(dir, mode, 0, 0));
        env.BecomeUser();
        env.WriteString(PathPtr, "/dir");
        var fd = await env.Call("SysOpen", PathPtr, (uint)(FileFlags.O_PATH | FileFlags.O_DIRECTORY));
        Assert.True(fd >= 0);
        Assert.Equal(expected, await env.Call("SysFchdir", (uint)fd));
        if (expected == 0) Assert.Same(env.Sm.GetFD(fd)!.Dentry, env.Sm.CurrentWorkingDirectory.Dentry);
    }

    private const int ThreadFlags = (int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_SIGHAND |
                                         LinuxConstants.CLONE_THREAD | LinuxConstants.CLONE_FILES);

    private sealed class AtomicWaitPipe(KernelScheduler scheduler) : PipeInode(scheduler)
    {
        public int RequestedMinWritableBytes { get; private set; }

        public override ValueTask<AwaitResult> WaitForWrite(LinuxFile file, FiberTask task, int minWritableBytes = 1)
        {
            RequestedMinWritableBytes = minWritableBytes;
            // Fail deterministically instead of hanging the test runner on the old busy loop.
            return minWritableBytes < 2
                ? ValueTask.FromResult(AwaitResult.Interrupted)
                : base.WaitForWrite(file, task, minWritableBytes);
        }
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();
        public Engine Engine { get; }
        public VMAManager Mem { get; }
        public SyscallManager Sm { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; } = new();
        public FiberTask Task { get; }

        public TestEnv(bool overlay = false)
        {
            Engine = _runtime.CreateEngine();
            Mem = _runtime.CreateAddressSpace();
            Sm = new SyscallManager(Engine, Mem, 0);
            Process = new Process(100, Mem, Sm);
            Task = new FiberTask(100, Process, Engine, Scheduler);
            var type = new FileSystemType { Name = "tmpfs" };
            var lower = new Tmpfs(memoryContext: _runtime.MemoryContext).ReadSuper(type, 0, "review-root", null);
            SuperBlock root = lower;
            if (overlay)
            {
                var file = new Dentry(FsName.FromString("lower-file"), null, lower.Root, lower);
                Assert.Equal(0, lower.Root.Inode!.Create(file, 0x1B6, 0, 0));
                var upper = new Tmpfs(memoryContext: _runtime.MemoryContext).ReadSuper(type, 0, "review-upper", null);
                root = new OverlayFileSystem(memoryContext: _runtime.MemoryContext).ReadSuper(
                    new FileSystemType { Name = "overlay" }, 0, "review-overlay",
                    new OverlayMountOptions { Lower = lower, Upper = upper });
            }
            Sm.InitializeRoot(root.Root, new Mount(root, root.Root));
            for (var addr = PathPtr; addr <= InfoPtr; addr += 4096)
            {
                Mem.Mmap(addr, 4096, Protection.Read | Protection.Write,
                    MapFlags.Fixed | MapFlags.Private | MapFlags.Anonymous, null, 0, "[review]", Engine);
                Assert.True(Mem.HandleFault(addr, true, Engine));
            }
        }

        public void Dispose() => Sm.Close();
        public void SwitchTo(FiberTask task) => task.Process.Syscalls.CurrentSyscallEngine = task.CPU;
        public void BecomeUser() => Process.UID = Process.EUID = Process.FSUID = 1000;
        public void Write(uint address, byte[] bytes) => Assert.True(Engine.CopyToUser(address, bytes));
        public void WriteString(uint address, string value) => Write(address, Encoding.UTF8.GetBytes(value + "\0"));

        public void WriteIovecs(params (uint Address, uint Length)[] iovs)
        {
            var bytes = new byte[iovs.Length * 8];
            for (var i = 0; i < iovs.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8), iovs[i].Address);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), iovs[i].Length);
            }
            Write(IovPtr, bytes);
        }

        public int AddFile(string name, string text, int mode = 0x1B6, FileFlags flags = FileFlags.O_RDWR)
        {
            SwitchTo(Task);
            var root = Sm.Root.Dentry!;
            var dentry = new Dentry(FsName.FromString(name), null, root, root.SuperBlock);
            Assert.Equal(0, root.Inode!.Create(dentry, mode, 0, 0));
            var file = new LinuxFile(dentry, flags, Sm.RootMount);
            var bytes = Encoding.ASCII.GetBytes(text);
            Assert.Equal(bytes.Length, dentry.Inode!.WriteFromHost(Task, file, bytes, 0));
            return Sm.AllocFD(file);
        }

        public string FileContents(int fd)
        {
            var file = Sm.GetFD(fd)!;
            var bytes = new byte[(int)file.OpenedInode!.Size];
            Assert.Equal(bytes.Length, file.OpenedInode.ReadToHost(Task, file, bytes, 0));
            return Encoding.ASCII.GetString(bytes);
        }

        public (PipeInode Pipe, int Reader, int Writer) AddPipe(PipeInode? pipe = null)
        {
            pipe ??= new PipeInode(Scheduler);
            pipe.SuperBlock = Sm.MemfdSuperBlock;
            var dentry = new Dentry(FsName.FromString("pipe"), pipe, null, Sm.MemfdSuperBlock);
            return (pipe, Sm.AllocFD(new LinuxFile(dentry, FileFlags.O_RDONLY, Sm.AnonMount)),
                Sm.AllocFD(new LinuxFile(dentry, FileFlags.O_WRONLY, Sm.AnonMount)));
        }

        public ValueTask<FiberTask> CloneThread()
        {
            SwitchTo(Task);
            return Task.Clone(ThreadFlags, 0, 0, 0, 0);
        }

        public ValueTask<int> Call(string method, uint a1 = 0, uint a2 = 0, uint a3 = 0,
            uint a4 = 0, uint a5 = 0, uint a6 = 0) => CallOn(Task, method, a1, a2, a3, a4, a5, a6);

        public ValueTask<int> CallOn(FiberTask task, string method, uint a1 = 0, uint a2 = 0, uint a3 = 0,
            uint a4 = 0, uint a5 = 0, uint a6 = 0)
        {
            SwitchTo(task);
            var sm = task.Process.Syscalls;
            var handler = typeof(SyscallManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
            return (ValueTask<int>)handler.Invoke(sm, [task.CPU, a1, a2, a3, a4, a5, a6])!;
        }

        public void DrainEvents() => typeof(KernelScheduler)
            .GetMethod("DrainEvents", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Scheduler, null);
    }
}
