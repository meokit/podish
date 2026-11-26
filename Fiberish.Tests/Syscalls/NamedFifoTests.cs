using System.Buffers;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

[Collection("ExternalPageManagerSerial")]
public class NamedFifoTests
{
    [Theory]
    [InlineData(FileFlags.O_RDONLY)]
    [InlineData(FileFlags.O_WRONLY)]
    public async Task BlockingOpen_ReservesUnpublishedFd_AndPairsWithPeer(FileFlags mode)
    {
        using var reader = new FifoEnv();
        using var writer = new FifoEnv(reader);
        var pending = reader.Open(mode).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Null(reader.Syscalls.GetFD(0));
        reader.Syscalls.WriteGuestFile("/file", [42], 0x1ed);
        var regular = reader.Syscalls.AllocFD(new LinuxFile(reader.Syscalls.ResolvePath("/file").loc,
            FileFlags.O_RDONLY));
        Assert.Equal(1, regular);
        var peerMode = mode == FileFlags.O_RDONLY ? FileFlags.O_WRONLY : FileFlags.O_RDONLY;
        var peer = await writer.Open(peerMode | FileFlags.O_NONBLOCK);
        Assert.True(peer >= 0);
        // A brief connection must satisfy rendezvous even if already closed on resume.
        writer.Syscalls.FreeFD(peer);
        reader.Drain();
        Assert.True(pending.IsCompleted);
        var fd = await pending;
        Assert.Equal(0, fd);
        Assert.Same(reader.Inode, reader.Syscalls.GetFD(fd)!.OpenedInode);
    }

    [Fact]
    public async Task NonblockingOpen_UsesActualEndpointCounts_NotPathOnlyHandles()
    {
        using var env = new FifoEnv();
        var path = await env.Open(FileFlags.O_PATH);
        Assert.True(path >= 0);
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(-(int)Errno.ENXIO, await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK));
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        Assert.True(w >= 0);
        var file = env.Syscalls.GetFD(r)!;
        var pipe = Assert.IsType<PipeInode>(file.IoInode);
        Assert.Equal(-(int)Errno.EAGAIN, pipe.ReadToHost(env.Task, file, new byte[1], 0));
        env.Syscalls.FreeFD(w);
        Assert.Equal(0, pipe.ReadToHost(env.Task, file, new byte[1], 0));
        env.Syscalls.FreeFD(r);
        Assert.Null(env.Inode.NamedPipe);
        Assert.False(env.Inode.IsFinalized);
    }

    [Theory]
    [InlineData(FileFlags.O_RDONLY)]
    [InlineData(FileFlags.O_WRONLY)]
    public void RetiringBlockedOpen_ReleasesUnpublishedFileAndTransport(FileFlags mode)
    {
        using var env = new FifoEnv();
        var pending = env.Open(mode);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, env.Inode.FileOpenRefCount);
        env.Task.Exited = true;
        env.Scheduler.DetachTask(env.Task);
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(0, env.Inode.FileOpenRefCount);
        Assert.False(env.Inode.IsFinalized);
        // Retirement intentionally never resumes the canceled guest syscall.
    }

    [Theory]
    [InlineData(FileFlags.O_RDONLY)]
    [InlineData(FileFlags.O_WRONLY)]
    public async Task SignalInterruptsBlockedOpen_WithoutPhantomPeer(FileFlags mode)
    {
        using var env = new FifoEnv();
        env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x1234 };
        var pending = env.Open(mode).AsTask();
        Assert.False(pending.IsCompleted);
        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(-(int)Errno.ERESTARTSYS, await pending);
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(0, env.Inode.FileOpenRefCount);
        Assert.Equal(-(int)Errno.ENXIO, await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK));
    }

    [Fact]
    public async Task Readiness_TracksPerOpenWriterGeneration()
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(r)!;
        var pipe = Assert.IsType<PipeInode>(file.IoInode);
        Assert.Equal(0, pipe.Poll(file, LinuxConstants.POLLIN));
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        env.Syscalls.FreeFD(w);
        Assert.Equal(PollEvents.POLLHUP, pipe.Poll(file, 0));
        var fresh = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var freshFile = env.Syscalls.GetFD(fresh)!;
        Assert.Equal(0, pipe.Poll(freshFile, LinuxConstants.POLLIN));
        w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        Assert.Equal(0, pipe.Poll(file, LinuxConstants.POLLIN));
        env.Syscalls.FreeFD(w);
        Assert.Equal(PollEvents.POLLHUP, pipe.Poll(freshFile, 0));
    }

    [Fact]
    public async Task ReadOnlyMount_AllowsExistingFifoTransport()
    {
        using var env = new FifoEnv();
        env.Mount.Flags |= LinuxConstants.MS_RDONLY;
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        Assert.True(fd >= 0);
        var file = env.Syscalls.GetFD(fd)!;
        Assert.Equal(0, file.WantWrite());
        Assert.Equal(1, file.IoInode!.WriteFromHost(env.Task, file, [42], 0));
        var bytes = new byte[1];
        Assert.Equal(1, file.IoInode.ReadToHost(env.Task, file, bytes, 0));
        Assert.Equal(42, bytes[0]);
        Assert.Equal(0UL, env.Inode.Size);
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)LinuxConstants.POLLIN)]
    public async Task RegisteredReadiness_DoesNotSpinOnInitialEof_AndWakesForHup(short events)
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(r)!;
        var pipe = Assert.IsType<PipeInode>(file.IoInode);
        var fired = 0;
        using var registration = pipe.RegisterWaitHandle(file, env.Task, () => fired++, events);
        Assert.NotNull(registration);
        env.Drain();
        Assert.Equal(0, fired);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        env.Syscalls.FreeFD(w);
        env.Drain();
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task DupKeepsStreamAlive_FinalCloseDiscardsDataButKeepsMetadata()
    {
        using var env = new FifoEnv();
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(fd)!;
        var pipe = file.IoInode!;
        Assert.Equal(1, pipe.WriteFromHost(env.Task, file, [42], 0));
        var dup = env.Syscalls.DupFD(fd);
        env.Syscalls.FreeFD(fd);
        Assert.Same(pipe, env.Inode.NamedPipe);
        env.Syscalls.FreeFD(dup);
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(1, env.Inode.LinkCount);
        Assert.False(env.Inode.IsFinalized);
        fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        file = env.Syscalls.GetFD(fd)!;
        Assert.NotSame(pipe, file.IoInode);
        Assert.Equal(-(int)Errno.EAGAIN, file.IoInode!.ReadToHost(env.Task, file, new byte[1], 0));
    }

    [Fact]
    public async Task DescriptorExhaustion_DoesNotAllocateOrPublishTransport()
    {
        using var env = new FifoEnv();
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE, new ResourceLimit(0, 1024));
        Assert.Equal(-(int)Errno.EMFILE, await env.Open(FileFlags.O_RDONLY));
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(0, env.Inode.FileOpenRefCount);
    }

    [Theory]
    [InlineData((int)FifoOpenCheckpoint.Reserved)]
    [InlineData((int)FifoOpenCheckpoint.Attached)]
    [InlineData((int)FifoOpenCheckpoint.BeforePublish)]
    public async Task InjectedAllocationFailure_RollsBackAndAllowsReopen(int checkpoint)
    {
        using var env = new FifoEnv();
        env.Syscalls.FifoOpenFaultForTesting = stage =>
        {
            if ((int)stage == checkpoint) throw new OutOfMemoryException("injected FIFO open failure");
        };
        Assert.Equal(-(int)Errno.ENOMEM, await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK));
        Assert.Null(env.Syscalls.GetFD(0));
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(0, env.Inode.FileOpenRefCount);
        env.Syscalls.FifoOpenFaultForTesting = null;
        Assert.Equal(0, await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK));
    }

    [Theory]
    [InlineData("silkfs")]
    [InlineData("overlay-silkfs")]
    public async Task PersistentBackend_RemountKeepsMetadataButNotPayload(string backend)
    {
        using var workspace = new TestWorkspace("fifo-persistence-");
        ulong ino;
        using (var env = new FifoEnv(backend: backend, source: workspace.RootPath))
        {
            var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
            var file = env.Syscalls.GetFD(fd)!;
            ino = env.Inode.Ino;
            var reader = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
            var readFile = env.Syscalls.GetFD(reader)!;
            Assert.Same(file.IoInode, readFile.IoInode);
            Assert.Equal(3, file.IoInode!.WriteFromHost(env.Task, file, "abc"u8, 0));
            var bytes = new byte[1];
            Assert.Equal(1, readFile.IoInode!.ReadToHost(env.Task, readFile, bytes, 0));
            Assert.Equal((byte)'a', bytes[0]);
            Assert.Equal(0UL, env.Inode.Size);
            // Leave unread payload; shutdown/final close must discard it.
        }
        using (var env = new FifoEnv(backend: backend, source: workspace.RootPath, create: false))
        {
            Assert.Equal(InodeType.Fifo, env.Inode.Type);
            Assert.Equal(ino, env.Inode.Ino);
            Assert.Equal(0x180, env.Inode.Mode & 0x1ff);
            Assert.Equal(0UL, env.Inode.Size);
            Assert.Null(env.Inode.NamedPipe);
            var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
            var file = env.Syscalls.GetFD(fd)!;
            Assert.Equal(-(int)Errno.EAGAIN, file.IoInode!.ReadToHost(env.Task, file, new byte[1], 0));
        }
    }

    [Fact]
    public async Task PendingOpen_PublishAllocationFailure_RollsBackOnlyItsEndpoint()
    {
        using var reader = new FifoEnv();
        using var writer = new FifoEnv(reader);
        reader.Syscalls.FifoOpenFaultForTesting = stage =>
        {
            if (stage == FifoOpenCheckpoint.BeforePublish) throw new OutOfMemoryException("pending publish");
        };
        var pending = reader.Open(FileFlags.O_RDONLY).AsTask();
        var fd = await writer.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        Assert.True(fd >= 0);
        reader.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(-(int)Errno.ENOMEM, await pending);
        Assert.Null(reader.Syscalls.GetFD(0));
        var file = writer.Syscalls.GetFD(fd)!;
        Assert.Equal(-(int)Errno.EPIPE, file.IoInode!.WriteFromHost(writer.Task, file, "x"u8, 0));
        writer.Syscalls.FreeFD(fd);
        Assert.Null(reader.Inode.NamedPipe);
        reader.Syscalls.FifoOpenFaultForTesting = null;
        Assert.Equal(0, await reader.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK));
    }

    [Fact]
    public async Task WarmedFifoTransport_ReadWriteAndReadiness_DoNotAllocate()
    {
        using var env = new FifoEnv();
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(fd)!;
        var pipe = file.IoInode!;
        var buffer = new byte[1];
        for (var i = 0; i < 32; i++) RoundTrip();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++) RoundTrip();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void RoundTrip()
        {
            pipe.WriteFromHost(env.Task, file, buffer, 0);
            pipe.Poll(file, LinuxConstants.POLLIN);
            pipe.ReadToHost(env.Task, file, buffer, 0);
        }
    }

    [Fact]
    public async Task WarmedFifoTransport_EmptyReadAndAtomicWritev_DoNotAllocate()
    {
        using var env = new FifoEnv();
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(fd)!;
        var pipe = Assert.IsType<PipeInode>(file.IoInode);
        const uint data = 0x61000080;
        Assert.True(env.Engine.CopyToUser(data, "abcd"u8));
        var iovs = new ArraySegment<Iovec>([
            new Iovec { BaseAddr = data, Len = 2 }, new Iovec { BaseAddr = data + 2, Len = 2 }
        ]);
        var buffer = new byte[4];
        for (var i = 0; i < 32; i++) RoundTrip();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = 0;
        for (var i = 0; i < 256; i++) error |= RoundTrip();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, error);
        Assert.Equal(0, allocated);

        int RoundTrip()
        {
            var empty = pipe.ReadToHost(env.Task, file, buffer, 0);
            var ready = pipe.Poll(file, LinuxConstants.POLLIN);
            var written = pipe.WriteV(env.Engine, file, env.Task, iovs, -1, 0);
            if (!written.IsCompletedSuccessfully) return 1;
            var count = written.GetAwaiter().GetResult();
            var read = pipe.ReadToHost(env.Task, file, buffer, 0);
            return empty == -(int)Errno.EAGAIN && ready == 0 && count == 4 && read == 4 ? 0 : 1;
        }
    }

    [Fact]
    public async Task InflightRead_RetainsEndpointAfterCloseFd()
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        var reader = env.Syscalls.GetFD(r)!;
        reader.Flags &= ~FileFlags.O_NONBLOCK;
        var pending = env.Call("SysRead", (uint)r, 0x61000080, 1).AsTask();
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(r);
        var writer = env.Syscalls.GetFD(w)!;
        Assert.Equal(1, writer.IoInode!.WriteFromHost(env.Task, writer, "x"u8, 0));
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(1, await pending);
        Assert.False(Assert.IsType<PipeInode>(writer.IoInode).HasReaders);
        env.Syscalls.FreeFD(w);
        Assert.Null(env.Inode.NamedPipe);
    }

    [Fact]
    public async Task InflightWrite_RetainsEndpointAfterCloseFd()
    {
        using var env = new FifoEnv();
        var w = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var writer = env.Syscalls.GetFD(w)!;
        var pipe = Assert.IsType<PipeInode>(writer.IoInode);
        Assert.Equal(4096, pipe.SetPipeSize(4096, true));
        var buffer = new byte[4096];
        Assert.Equal(4096, pipe.WriteFromHost(env.Task, writer, buffer, 0));
        writer.Flags &= ~FileFlags.O_NONBLOCK;
        Assert.True(env.Engine.CopyToUser(0x61000080, "x"u8));
        var pending = env.Call("SysWrite", (uint)w, 0x61000080, 1).AsTask();
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(w);
        var reader = env.Syscalls.GetFD(r)!;
        Assert.Equal(4096, pipe.ReadToHost(env.Task, reader, buffer, 0));
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(1, await pending);
        Assert.Equal(1, pipe.ReadToHost(env.Task, reader, buffer, 0));
        Assert.Equal((byte)'x', buffer[0]);
        Assert.Equal(PollEvents.POLLHUP, pipe.Poll(reader, 0));
    }

    [Fact]
    public void InflightRead_RetirementReleasesClosedDescriptorPin()
    {
        using var env = new FifoEnv();
        var r = env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK).GetAwaiter().GetResult();
        var w = env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK).GetAwaiter().GetResult();
        env.Syscalls.GetFD(r)!.Flags &= ~FileFlags.O_NONBLOCK;
        var pending = env.Call("SysRead", (uint)r, 0x61000080, 1);
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(r);
        env.Task.Exited = true;
        env.Scheduler.DetachTask(env.Task);
        Assert.Null(env.Inode.NamedPipe);
        Assert.Equal(0, env.Inode.FileOpenRefCount);
    }

    [Fact]
    public async Task InflightRead_SignalReleasesClosedDescriptorPin()
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        env.Syscalls.GetFD(r)!.Flags &= ~FileFlags.O_NONBLOCK;
        env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x1234 };
        var pending = env.Call("SysRead", (uint)r, 0x61000080, 1).AsTask();
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(r);
        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(-(int)Errno.ERESTARTSYS, await pending);
        var writer = env.Syscalls.GetFD(w)!;
        Assert.False(Assert.IsType<PipeInode>(writer.IoInode).HasReaders);
    }

    [Theory]
    [InlineData("tmpfs")]
    [InlineData("overlay-tmpfs")]
    [InlineData("overlay-silkfs")]
    public async Task HardLinkAliases_ShareTransportAcrossFreshLookup(string backend)
    {
        using var workspace = new TestWorkspace("fifo-alias-");
        using var env = new FifoEnv(backend: backend, source: workspace.RootPath);
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var reader = env.Syscalls.GetFD(r)!;
        Assert.True(env.Engine.CopyToUser(0x61000100, "/alias\0"u8));
        Assert.Equal(0, await env.Call("SysLink", 0x61000000, 0x61000100));
        var w = await env.Call("SysOpen", 0x61000100, (uint)(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK));
        Assert.True(w >= 0);
        var writer = env.Syscalls.GetFD(w)!;
        Assert.Equal(reader.OpenedInode!.Ino, writer.OpenedInode!.Ino);
        Assert.Same(reader.IoInode, writer.IoInode);
        Assert.Equal(1, writer.IoInode!.WriteFromHost(env.Task, writer, "x"u8, 0));
        var bytes = new byte[1];
        Assert.Equal(1, reader.IoInode!.ReadToHost(env.Task, reader, bytes, 0));
        env.Syscalls.FreeFD(w);
        env.Syscalls.FreeFD(r);
        Assert.Null(reader.OpenedInode.NamedPipe);
        Assert.Null(writer.OpenedInode.NamedPipe);
        Assert.Equal(-(int)Errno.ENXIO, await env.Call("SysOpen", 0x61000100,
            (uint)(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK)));
    }

    [Theory]
    [InlineData("overlay-tmpfs", false)]
    [InlineData("overlay-silkfs", false)]
    [InlineData("overlay-tmpfs", true)]
    [InlineData("overlay-silkfs", true)]
    public async Task OverlayAliases_ReopenAfterFinalClose_AndCopyUpRetainsTransport(string backend, bool lowerFifo)
    {
        using var workspace = new TestWorkspace("fifo-reopen-");
        using var env = new FifoEnv(backend: backend, source: workspace.RootPath, lowerFifo: lowerFifo);
        var sb = Assert.IsType<OverlaySuperBlock>(env.Inode.SuperBlock);
        Assert.True(env.Engine.CopyToUser(0x61000100, "/alias\0"u8));
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var original = env.Syscalls.GetFD(r)!.IoInode;
        // Linking a lower FIFO copies its metadata up while its transport is live.
        Assert.Equal(0, await env.Call("SysLink", 0x61000000, 0x61000100));
        var w = await env.Call("SysOpen", 0x61000100, (uint)(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK));
        Assert.True(w >= 0);
        Assert.Same(original, env.Syscalls.GetFD(w)!.IoInode);
        Assert.Equal(1, original!.WriteFromHost(env.Task, env.Syscalls.GetFD(w)!, "x"u8, 0));
        env.Syscalls.FreeFD(r);
        env.Syscalls.FreeFD(w);
        Assert.Equal(0, FifoRegistryCount(sb));

        // Reopen in the opposite order: a cached empty alias must join the newly
        // active identity rather than overwrite it with its old state.
        r = await env.Call("SysOpen", 0x61000100, (uint)(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK));
        w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        Assert.True(w >= 0);
        var reader = env.Syscalls.GetFD(r)!;
        var writer = env.Syscalls.GetFD(w)!;
        Assert.NotSame(original, reader.IoInode);
        Assert.Same(reader.IoInode, writer.IoInode);
        var bytes = new byte[1];
        Assert.Equal(-(int)Errno.EAGAIN, reader.IoInode!.ReadToHost(env.Task, reader, bytes, 0));
        Assert.Equal(1, writer.IoInode!.WriteFromHost(env.Task, writer, "y"u8, 0));
        Assert.Equal(1, reader.IoInode.ReadToHost(env.Task, reader, bytes, 0));
        Assert.Equal((byte)'y', bytes[0]);
        env.Syscalls.FreeFD(r);
        env.Syscalls.FreeFD(w);
        Assert.Equal(0, FifoRegistryCount(sb));

        static int FifoRegistryCount(OverlaySuperBlock sb) =>
            (typeof(OverlaySuperBlock).GetField("_fifoStates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sb) as System.Collections.IDictionary)?.Count ?? 0;
    }

    [Fact]
    public async Task AsyncReader_FcntlEnableDisable_UpdatesSignalSubscribers()
    {
        using var env = new FifoEnv();
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(fd)!;
        var pipe = Assert.IsType<PipeInode>(file.IoInode);
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 8, (uint)env.Process.TGID)); // F_SETOWN
        var buffer = new byte[1];
        CheckSignal(false);
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 4, (uint)FileFlags.O_NONBLOCK | 0x2000));
        CheckSignal(true);
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, 4, (uint)FileFlags.O_NONBLOCK));
        CheckSignal(false);

        void CheckSignal(bool expected)
        {
            Assert.Equal(1, pipe.WriteFromHost(env.Task, file, buffer, 0));
            var mask = 1UL << ((int)Signal.SIGIO - 1);
            Assert.Equal(expected, (env.Task.GetVisiblePendingSignals() & mask) != 0);
            env.Task.PendingSignals &= ~mask;
            env.Process.PendingProcessSignals &= ~mask;
            Assert.Equal(1, pipe.ReadToHost(env.Task, file, buffer, 0));
        }
    }

    [Theory]
    [InlineData("SysSplice", "complete")]
    [InlineData("SysTee", "complete")]
    [InlineData("SysSendfile", "complete")]
    [InlineData("SysSplice", "signal")]
    [InlineData("SysTee", "signal")]
    [InlineData("SysSendfile", "signal")]
    [InlineData("SysSplice", "retire")]
    [InlineData("SysTee", "retire")]
    [InlineData("SysSendfile", "retire")]
    public async Task BlockedCopy_PinsBothOpenDescriptionsAcrossCloseAndFdReuse(string syscall, string finish)
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        var input = env.Syscalls.GetFD(r)!;
        input.Flags &= ~FileFlags.O_NONBLOCK;
        var child = new Dentry(FsName.FromString("output"), null, env.Mount.Root, env.Mount.Root.SuperBlock);
        Assert.Equal(0, env.Mount.Root.Inode!.Mknod(child, 0x180, 0, 0, InodeType.Fifo, 0));
        Assert.True(env.Engine.CopyToUser(0x61000100, "/output\0"u8));
        var o = await env.Call("SysOpen", 0x61000100, (uint)(FileFlags.O_RDWR | FileFlags.O_NONBLOCK));
        var output = env.Syscalls.GetFD(o)!;
        var outPipe = Assert.IsType<PipeInode>(output.IoInode);
        var observerFd = await env.Call("SysOpen", 0x61000100, (uint)(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK));
        var observer = env.Syscalls.GetFD(observerFd)!;
        var pending = (syscall switch
        {
            "SysSplice" => env.Call(syscall, (uint)r, 0, (uint)o, 0, 1),
            "SysTee" => env.Call(syscall, (uint)r, (uint)o, 1),
            _ => env.Call(syscall, (uint)o, (uint)r, 0, 1)
        }).AsTask();
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(r);
        env.Syscalls.FreeFD(o);
        Assert.Equal(0, outPipe.Poll(observer, 0) & PollEvents.POLLHUP);
        env.Syscalls.WriteGuestFile("/reused", [], 0x180);
        Assert.Equal(r, env.Syscalls.AllocFD(new LinuxFile(env.Syscalls.ResolvePath("/reused").loc, FileFlags.O_RDWR)));

        if (finish == "retire")
        {
            env.Task.Exited = true;
            env.Scheduler.DetachTask(env.Task);
            Assert.Null(env.Inode.NamedPipe);
            Assert.Null(output.OpenedInode!.NamedPipe);
            return;
        }
        if (finish == "signal")
        {
            env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x1234 };
            env.Task.PostSignal((int)Signal.SIGUSR1);
        }
        else
        {
            var writer = env.Syscalls.GetFD(w)!;
            Assert.Equal(1, writer.IoInode!.WriteFromHost(env.Task, writer, "x"u8, 0));
        }
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(finish == "signal" ? -(int)Errno.ERESTARTSYS : 1, await pending);
        Assert.Equal(PollEvents.POLLHUP, outPipe.Poll(observer, 0) & PollEvents.POLLHUP);
        if (finish == "complete")
        {
            var bytes = new byte[1];
            Assert.Equal(1, outPipe.ReadToHost(env.Task, observer, bytes, 0));
            Assert.Equal((byte)'x', bytes[0]);
        }
        Assert.Equal(InodeType.File, env.Syscalls.GetFD(r)!.OpenedInode!.Type);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("signal")]
    [InlineData("retire")]
    public async Task BlockedVmsplice_PinsReadEndpointAcrossClose(string finish)
    {
        using var env = new FifoEnv();
        var r = await env.Open(FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        var w = await env.Open(FileFlags.O_WRONLY | FileFlags.O_NONBLOCK);
        env.Syscalls.GetFD(r)!.Flags &= ~FileFlags.O_NONBLOCK;
        Assert.True(env.Engine.CopyToUser(0x61000100, new byte[] { 0x80, 0, 0, 0x61, 1, 0, 0, 0 }));
        var pending = env.Call("SysVmsplice", (uint)r, 0x61000100, 1).AsTask();
        Assert.False(pending.IsCompleted);
        env.Syscalls.FreeFD(r);
        if (finish == "retire")
        {
            env.Task.Exited = true;
            env.Scheduler.DetachTask(env.Task);
            Assert.Null(env.Inode.NamedPipe);
            return;
        }
        if (finish == "signal")
        {
            env.Process.SignalActions[(int)Signal.SIGUSR1] = new SigAction { Handler = 0x1234 };
            env.Task.PostSignal((int)Signal.SIGUSR1);
        }
        else
        {
            var writer = env.Syscalls.GetFD(w)!;
            Assert.Equal(1, writer.IoInode!.WriteFromHost(env.Task, writer, "x"u8, 0));
        }
        env.Drain();
        Assert.True(pending.IsCompleted);
        Assert.Equal(finish == "signal" ? -(int)Errno.ERESTARTSYS : 1, await pending);
        Assert.False(Assert.IsType<PipeInode>(env.Syscalls.GetFD(w)!.IoInode).HasReaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RentedIoBuffer_ReturnsExactlyOnce_OnCompletionOrRetirement(bool retire)
    {
        using var env = new FifoEnv();
        var pool = new CountingBufferPool();
        var rented = new TaskIoBuffer<byte>(pool.Rent(16), pool);
        rented.TrackPending(env.Task);
        Assert.Equal(0, pool.Returns);
        if (retire)
        {
            env.Task.Exited = true;
            env.Scheduler.DetachTask(env.Task);
            Assert.Equal(1, pool.Returns); // No suspended async finally was needed.
        }
        rented.Dispose();
        rented.Dispose();
        Assert.Equal(1, pool.Returns);
    }

    private sealed class CountingBufferPool : ArrayPool<byte>
    {
        public int Returns { get; private set; }
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returns++;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmedEpollWatchedTransport_DoesNotAllocate(bool edge)
    {
        using var env = new FifoEnv();
        var fd = await env.Open(FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        var file = env.Syscalls.GetFD(fd)!;
        var pipe = file.IoInode!;
        var epoll = new EpollInode(901, env.Inode.SuperBlock, env.Scheduler);
        var buffer = new byte[1];
        var events = new byte[12];
        Assert.Equal(0, epoll.Ctl(env.Task, LinuxConstants.EPOLL_CTL_ADD, fd, file,
            (uint)(LinuxConstants.EPOLLIN | (edge ? LinuxConstants.EPOLLET : 0)), 1));
        try
        {
            for (var i = 0; i < 32; i++) Assert.Equal(1, RoundTrip());
            var before = GC.GetAllocatedBytesForCurrentThread();
            var delivered = 0;
            for (var i = 0; i < 256; i++) delivered += RoundTrip();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(256, delivered);
            Assert.Equal(0, allocated);
        }
        finally { epoll.Release(null!); }

        int RoundTrip()
        {
            pipe.WriteFromHost(env.Task, file, buffer, 0);
            var count = epoll.TryHarvestNow(events, 1);
            pipe.ReadToHost(env.Task, file, buffer, 0);
            return count;
        }
    }

    private sealed class FifoEnv : IDisposable
    {
        private const uint PathAddr = 0x61000000;
        private readonly TestRuntimeFactory _runtime = new();
        private readonly Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> _open;
        private readonly SynchronizationContext _context;
        private static readonly MethodInfo DrainEvents = typeof(KernelScheduler)
            .GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public FifoEnv(FifoEnv? shared = null, string backend = "tmpfs", string? source = null, bool create = true,
            bool lowerFifo = false)
        {
            Engine = _runtime.CreateEngine();
            var mem = _runtime.CreateAddressSpace();
            Syscalls = new SyscallManager(Engine, mem, 0);
            if (shared == null)
            {
                if (backend is "overlay-silkfs" or "overlay-tmpfs")
                {
                    var lowerType = FileSystemRegistry.Get("layerfs")!;
                    var index = new LayerIndex();
                    if (lowerFifo) index.AddEntry(new LayerIndexEntry("/fifo", InodeType.Fifo, 0x180));
                    var lower = lowerType.CreateAnonymousFileSystem().ReadSuper(lowerType, 0, "fifo-lower",
                        new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                    Syscalls.MountRootOverlayWithLower(lower, backend == "overlay-silkfs" ? "silkfs" : "tmpfs", source!);
                    Mount = Syscalls.RootMount!;
                }
                else
                {
                    var type = FileSystemRegistry.Get(backend)!;
                    var sb = type.CreateAnonymousFileSystem().ReadSuper(type, 0, source ?? "fifo-test", null);
                    Mount = new Mount(sb, sb.Root) { FsType = backend };
                }
                if (create && !lowerFifo)
                {
                    var child = new Dentry(FsName.FromString("fifo"), null, Mount.Root, Mount.Root.SuperBlock);
                    Assert.Equal(0, Mount.Root.Inode!.Mknod(child, 0x180, 0, 0, InodeType.Fifo, 0));
                }
                Inode = Mount.Root.Inode!.Lookup("fifo")!.Inode!;
            }
            else
            {
                Mount = shared.Mount;
                Inode = shared.Inode;
            }
            Syscalls.InitializeRoot(Mount.Root, Mount);
            Scheduler = shared?.Scheduler ?? new KernelScheduler();
            Process = new Process(shared == null ? 9701 : 9702, mem, Syscalls);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(Process.TGID, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Engine.CurrentSyscallManager = Syscalls;
            _context = (SynchronizationContext)typeof(KernelScheduler)
                .GetMethod("GetSynchronizationContextFor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Scheduler, [Task])!;
            mem.Mmap(PathAddr, 4096, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "fifo-path", Engine);
            Assert.True(mem.HandleFault(PathAddr, true, Engine));
            Assert.True(Engine.CopyToUser(PathAddr, "/fifo\0"u8));
            _open = typeof(SyscallManager).GetMethod("SysOpen", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>>>(Syscalls);
        }

        public Engine Engine { get; }
        public SyscallManager Syscalls { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public Mount Mount { get; }
        public Inode Inode { get; }
        public ValueTask<int> Open(FileFlags flags)
            => Call(_open, PathAddr, (uint)flags);

        public ValueTask<int> Call(string name, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return Call((engine, first, second, third, fourth, fifth, sixth) =>
                (ValueTask<int>)method.Invoke(Syscalls, [engine, first, second, third, fourth, fifth, sixth])!,
                a1, a2, a3, a4, a5);
        }

        private ValueTask<int> Call(Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> syscall,
            uint a1, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0)
        {
            var previous = SynchronizationContext.Current;
            var oldTask = Scheduler.CurrentTask;
            SynchronizationContext.SetSynchronizationContext(_context);
            Scheduler.CurrentTask = Task;
            try { return syscall(Engine, a1, a2, a3, a4, a5, 0); }
            finally
            {
                Scheduler.CurrentTask = oldTask;
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
        public void Drain()
        {
            for (var i = 0; i < 32; i++)
                if (!(bool)DrainEvents.Invoke(Scheduler, null)!) break;
        }
        public void Dispose()
        {
            Syscalls.Close();
            Engine.Dispose();
        }
    }
}
