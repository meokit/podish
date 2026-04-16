using System.Reflection;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsPageCacheWritebackTests
{
    private readonly TestRuntimeFactory _runtime = new();
    private static readonly HostMemoryMapGeometry BufferedOnlyGeometry =
        new(LinuxConstants.PageSize, LinuxConstants.PageSize, LinuxConstants.PageSize, false, false);
    private static readonly bool AsyncFsyncDebugEnabled =
        Environment.GetEnvironmentVariable("FIBERISH_DEBUG_BLOCKING_HOST_OP") == "1";

    [Fact]
    public void MapShared_SyncVma_WritesBackPageCacheToHostFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x41000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            Assert.True(engine.CopyToUser(mapAddr + 1, "ABC"u8.ToArray()));

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            VMAManager.SyncVmArea(vma!, engine, mapAddr, mapAddr + LinuxConstants.PageSize);

            Assert.Equal("hABCo", File.ReadAllText(hostFile));
            file.Dentry.Inode!.Release(file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MapShared_Munmap_WritebackOnlyFlushesHostMappedWindowBeforeUnmap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-munmap-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "world");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x42000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            Assert.True(engine.CopyToUser(mapAddr + 2, "XY"u8.ToArray()));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            Assert.Equal("woXYd", File.ReadAllText(hostFile));
            file.Dentry.Inode!.Release(file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MapShared_Munmap_WritebackOnlyFlushesAllocatedPageCacheBeforeUnmap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-munmap-buffered-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "world");

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var file = OpenHostFile(root, "data.bin", runtime.MemoryContext);

            const uint mapAddr = 0x42100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            Assert.True(engine.CopyToUser(mapAddr + 2, "XY"u8.ToArray()));
            Assert.Equal("world", File.ReadAllText(hostFile));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            Assert.Equal("woXYd", File.ReadAllText(hostFile));
            file.Dentry.Inode!.Release(file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_WritesBackMappedDirtyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var rc = await CallSys(sm, engine, "SysFsync", (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));
            sm.FreeFD(fd);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_WritesBackDirtyPagesFromPeerThreadEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-peer-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();

            var process = new Process(5001, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5001, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var rc = await CallSys(sm, engine, "SysFsync", (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            GC.KeepAlive(peer);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Munmap_WritebackOnlyFlushesDirtyHostMappedPagesFromPeerThreadEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-munmap-peer-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();

            var process = new Process(5002, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5002, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);

            const uint mapAddr = 0x43200000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            ProcessAddressSpaceSync.Munmap(mm, engine, mapAddr, LinuxConstants.PageSize, process);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            GC.KeepAlive(peer);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_AfterPeerThreadExit_StillWritesBackDirtyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-peer-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();

            var process = new Process(5003, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5003, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43300000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            _ = scheduler.DetachTask(peer);

            var rc = await CallSys(sm, engine, "SysFsync", (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Fsync_AsyncDurableFlush_MustNotBlockUnrelatedSchedulerWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-async-yield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(root);
        var scheduler = new KernelScheduler();
        var process = new Process(5010, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(5010, process, engine, scheduler);
        scheduler.RegisterTask(task);
        task.Status = FiberTaskStatus.Waiting;
        task.ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
        engine.Owner = task;

        var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
        loc.Dentry!.Inode!.Open(file);
        var fd = sm.AllocFD(file);

        using var flushStarted = new ManualResetEventSlim(false);
        using var flushRelease = new ManualResetEventSlim(false);
        var fsyncTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherWorkTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HostInode.FlushToDiskOverrideForTests = _ =>
        {
            TraceAsyncFsyncTest("flush override entered");
            flushStarted.Set();
            flushRelease.Wait();
            TraceAsyncFsyncTest("flush override released");
            return 0;
        };

        TraceAsyncFsyncTest("starting scheduler thread");
        var schedulerThread = Task.Run(() => scheduler.Run());
        try
        {
            TraceAsyncFsyncTest("waiting for scheduler ready");
            Assert.True(WaitForSchedulerReady(scheduler, task, TimeSpan.FromSeconds(1)));

            TraceAsyncFsyncTest("queueing fsync action");
            scheduler.ScheduleFromAnyThread(() =>
                StartScheduledAction(() => CallSys(sm, engine, "SysFsync", (uint)fd), fsyncTcs), task);

            Assert.True(flushStarted.Wait(1000), "FlushToDisk worker did not start in time.");
            TraceAsyncFsyncTest("flush worker started; scheduling unrelated work");

            scheduler.ScheduleFromAnyThread(() => otherWorkTcs.TrySetResult(), task);
            Assert.True(await Task.WhenAny(otherWorkTcs.Task, Task.Delay(1000)) == otherWorkTcs.Task,
                "Scheduler did not process unrelated work while fsync flush was blocked.");

            TraceAsyncFsyncTest("releasing flush worker");
            flushRelease.Set();
            Assert.Equal(0, await fsyncTcs.Task);
            TraceAsyncFsyncTest("fsync action completed");
            Assert.Equal("abcde", File.ReadAllText(hostFile));

            scheduler.ScheduleFromAnyThread(() =>
            {
                sm.FreeFD(fd);
                closeTcs.TrySetResult();
            }, task);
            await closeTcs.Task;
        }
        finally
        {
            HostInode.FlushToDiskOverrideForTests = null;
            flushRelease.Set();
            await StopSchedulerAsync(scheduler, task, schedulerThread);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Fsync_AsyncDurableFlush_MustKeepHandleAliveWhenFdClosesMidFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-async-handle-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        sm.MountRootHostfs(root);
        var scheduler = new KernelScheduler();
        var process = new Process(5020, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(5020, process, engine, scheduler);
        scheduler.RegisterTask(task);
        task.Status = FiberTaskStatus.Waiting;
        task.ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
        engine.Owner = task;

        var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
        loc.Dentry!.Inode!.Open(file);
        var fd = sm.AllocFD(file);

        using var flushStarted = new ManualResetEventSlim(false);
        using var flushRelease = new ManualResetEventSlim(false);
        var fsyncTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HostInode.FlushToDiskOverrideForTests = _ =>
        {
            TraceAsyncFsyncTest("flush override entered");
            flushStarted.Set();
            flushRelease.Wait();
            TraceAsyncFsyncTest("flush override released");
            return 0;
        };

        TraceAsyncFsyncTest("starting scheduler thread");
        var schedulerThread = Task.Run(() => scheduler.Run());
        try
        {
            TraceAsyncFsyncTest("waiting for scheduler ready");
            Assert.True(WaitForSchedulerReady(scheduler, task, TimeSpan.FromSeconds(1)));

            TraceAsyncFsyncTest("queueing fsync action");
            scheduler.ScheduleFromAnyThread(() =>
                StartScheduledAction(() => CallSys(sm, engine, "SysFsync", (uint)fd), fsyncTcs), task);

            Assert.True(flushStarted.Wait(1000), "FlushToDisk worker did not start in time.");
            TraceAsyncFsyncTest("flush worker started; closing fd");

            scheduler.ScheduleFromAnyThread(() =>
            {
                sm.FreeFD(fd);
                closeTcs.TrySetResult();
            }, task);
            await closeTcs.Task;

            TraceAsyncFsyncTest("releasing flush worker");
            flushRelease.Set();
            Assert.Equal(0, await fsyncTcs.Task);
            TraceAsyncFsyncTest("fsync action completed");
            Assert.Equal("abcde", File.ReadAllText(hostFile));
        }
        finally
        {
            HostInode.FlushToDiskOverrideForTests = null;
            flushRelease.Set();
            await StopSchedulerAsync(scheduler, task, schedulerThread);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesAcrossFds()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "12345");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            _ = sm.AllocFD(file);

            const uint mapAddr = 0x44000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 2, "QQ"u8.ToArray()));

            var rc = await CallSys(sm, engine, "SysSync");
            Assert.Equal(0, rc);
            Assert.Equal("12QQ5", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesFromOtherProcessInSameContainer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-container-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        try
        {
            var runtime1 = new TestRuntimeFactory();
            var runtime2 = new TestRuntimeFactory();
            using var engine1 = runtime1.CreateEngine();
            using var engine2 = runtime2.CreateEngine();
            var mm1 = runtime1.CreateAddressSpace();
            var mm2 = runtime2.CreateAddressSpace();
            var sm1 = new SyscallManager(engine1, mm1, 0);
            var sm2 = new SyscallManager(engine2, mm2, 0);
            sm1.MountRootHostfs(root);
            sm2.MountRootHostfs(root);

            var scheduler = new KernelScheduler();

            scheduler.RegisterProcess(new Process(1001, mm1, sm1));
            scheduler.RegisterProcess(new Process(1002, mm2, sm2));

            var loc2 = sm2.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc2.IsValid);
            var file2 = new LinuxFile(loc2.Dentry!, FileFlags.O_RDWR, loc2.Mount!);
            loc2.Dentry!.Inode!.Open(file2);
            _ = sm2.AllocFD(file2);

            const uint mapAddr = 0x45000000;
            mm2.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, "MAP_SHARED", engine2);
            Assert.True(mm2.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys(sm1, engine1, "SysSync");
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesFromSharedAddressSpacePeer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-shared-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        try
        {
            using var engine1 = _runtime.CreateEngine();
            using var engine2 = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm1 = new SyscallManager(engine1, mm, 0);
            var sm2 = new SyscallManager(engine2, mm, 0);
            sm1.MountRootHostfs(root);
            sm2.MountRootHostfs(root);

            var scheduler = new KernelScheduler();


            var process1 = new Process(1011, mm, sm1);
            var process2 = new Process(1012, mm, sm2);
            scheduler.RegisterProcess(process1);
            scheduler.RegisterProcess(process2);

            var task1 = new FiberTask(1011, process1, engine1, scheduler);
            var task2 = new FiberTask(1012, process2, engine2, scheduler);
            engine1.Owner = task1;
            engine2.Owner = task2;

            var loc2 = sm2.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc2.IsValid);
            var file2 = new LinuxFile(loc2.Dentry!, FileFlags.O_RDWR, loc2.Mount!);
            loc2.Dentry!.Inode!.Open(file2);
            _ = sm2.AllocFD(file2);

            const uint mapAddr = 0x45200000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, "MAP_SHARED", engine2);
            Assert.True(mm.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys(sm1, engine1, "SysSync");
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fsync_WritesBackMappedDirtyPagesFromOtherProcessInSameContainer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-container-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        try
        {
            var runtime1 = new TestRuntimeFactory();
            var runtime2 = new TestRuntimeFactory();
            using var engine1 = runtime1.CreateEngine();
            using var engine2 = runtime2.CreateEngine();
            var mm1 = runtime1.CreateAddressSpace();
            var mm2 = runtime2.CreateAddressSpace();
            var sm1 = new SyscallManager(engine1, mm1, 0);
            sm1.MountRootHostfs(root);
            _ = new SyscallManager(engine2, mm2, 0);

            var loc1 = sm1.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc1.IsValid);
            var file1 = new LinuxFile(loc1.Dentry!, FileFlags.O_RDWR, loc1.Mount!);
            loc1.Dentry!.Inode!.Open(file1);
            var fd1 = sm1.AllocFD(file1);

            // Use the same dentry/mount object so both mappings target the exact same inode identity.
            var file2 = new LinuxFile(loc1.Dentry!, FileFlags.O_RDWR, loc1.Mount!);
            loc1.Dentry!.Inode!.Open(file2);
            var fd2 = sm1.AllocFD(file2);

            const uint mapAddr = 0x45100000;
            mm2.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, "MAP_SHARED", engine2);
            Assert.True(mm2.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys(sm1, engine1, "SysFsync", (uint)fd1);
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));

            sm1.FreeFD(fd1);
            sm1.FreeFD(fd2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fsync_MustFlushBufferedWritePageCacheForSameFd()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-write-buffered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            var mappingRef = ((MappingBackedInode)file.Dentry.Inode!).AcquireMappingRef();
            try
            {
                var writeRc = file.Dentry.Inode!.WriteFromHost(null, file, "XY"u8.ToArray(), 1);
                Assert.Equal(2, writeRc);
                Assert.Equal("aXYde", File.ReadAllText(hostFile));

                var fsyncRc = await CallSys(sm, engine, "SysFsync", (uint)fd);
                Assert.Equal(0, fsyncRc);
                Assert.Equal("aXYde", File.ReadAllText(hostFile));

                sm.FreeFD(fd);
            }
            finally
            {
                mappingRef.Release();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Close_MustFlushBufferedWritePageCacheWhenMappedBackendUnsupported()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-close-write-buffered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            var writeRc = file.Dentry.Inode!.WriteFromHost(null, file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, writeRc);
            Assert.Equal("abcde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            Assert.Equal("aXYde", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BufferedAppendWriteback_MustRespectPageOffsetsInsteadOfReappendingDirtyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-append-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, string.Empty);

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_WRONLY | FileFlags.O_APPEND, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);
            var inode = loc.Dentry.Inode!;

            Assert.Equal(1, inode.WriteFromHost(null, file, "A"u8.ToArray(), (long)inode.Size));
            Assert.Equal("A", File.ReadAllText(hostFile));

            Assert.Equal(1, inode.WriteFromHost(null, file, "B"u8.ToArray(), (long)inode.Size));
            Assert.Equal("AB", File.ReadAllText(hostFile));

            Assert.Equal(1, inode.WriteFromHost(null, file, "C"u8.ToArray(), (long)inode.Size));
            Assert.Equal("ABC", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            Assert.Equal("ABC", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Reopen_MustInvalidateCleanCachedPagesAfterHostRewrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-reopen-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        LinuxFile? first = null;
        LinuxFile? second = null;
        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            first = OpenHostFile(root, "data.bin", runtime.MemoryContext);
            var inode = Assert.IsType<HostInode>(first.Dentry.Inode);

            var warm = new byte[5];
            Assert.Equal(5, inode.ReadToHost(null, first, warm, 0));
            Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(warm));

            first.Close();
            first = null;

            File.WriteAllText(hostFile, "HELLO!!!");

            second = new LinuxFile(inode.Dentries[0], FileFlags.O_RDONLY, null!);
            second.Dentry.Inode!.Open(second);

            var readBack = new byte[8];
            var readRc = inode.ReadToHost(null, second, readBack, 0);
            Assert.Equal(8, readRc);
            Assert.Equal("HELLO!!!", System.Text.Encoding.ASCII.GetString(readBack));
        }
        finally
        {
            second?.Close();
            first?.Close();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DirtyLogicalSize_Size_MustExposeGuestLengthBeforeFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-dirty-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var inode = Assert.IsType<HostInode>(loc.Dentry.Inode);
            var mapping = inode.AcquireMappingRef();

            try
            {
                var warm = new byte[5];
                Assert.Equal(5, inode.ReadToHost(null, file, warm, 0));
                Assert.NotEqual(IntPtr.Zero, mapping.GetPage(0));

                inode.Size = 8;
                inode.SetPageDirty(1);
                mapping.MarkDirty(1);

                Assert.Equal(8UL, inode.Size);
                Assert.Equal(5L, new FileInfo(hostFile).Length);
            }
            finally
            {
                mapping.Release();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Close_MustFlushBufferedOutOfOrderWritesAcrossPageBoundaryWithoutShrinkingLogicalSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-out-of-order-cross-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, string.Empty);

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var fd = sm.AllocFD(file);

            var tailOffset = LinuxConstants.PageSize - 32;
            var tailWrite = new byte[64];
            Array.Fill(tailWrite, (byte)'T');
            var headWrite = "head"u8.ToArray();

            Assert.Equal(tailWrite.Length, inode.WriteFromHost(null, file, tailWrite, tailOffset));
            Assert.Equal((ulong)(tailOffset + tailWrite.Length), inode.Size);

            Assert.Equal(headWrite.Length, inode.WriteFromHost(null, file, headWrite, 0));
            Assert.Equal((ulong)(tailOffset + tailWrite.Length), inode.Size);

            sm.FreeFD(fd);

            var bytes = File.ReadAllBytes(hostFile);
            Assert.Equal(tailOffset + tailWrite.Length, bytes.Length);
            Assert.Equal(headWrite, bytes[..headWrite.Length]);
            Assert.All(bytes.AsSpan(headWrite.Length, tailOffset - headWrite.Length).ToArray(), b => Assert.Equal(0, b));
            Assert.Equal(tailWrite, bytes[tailOffset..(tailOffset + tailWrite.Length)]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EvictUnusedInodes_MustWriteBackBufferedDirtyPagesBeforeDroppingHostfsPageCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-evict-write-buffered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);
            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);

            var writeRc = hostInode.WriteFromHost(null, file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, writeRc);
            Assert.Equal("abcde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            Assert.Equal("aXYde", File.ReadAllText(hostFile));

            var pagePtr = hostInode.Mapping!.PeekPage(0);
            Assert.NotEqual(IntPtr.Zero, pagePtr);
            Marshal.WriteByte(pagePtr, 1, (byte)'Z');
            Marshal.WriteByte(pagePtr, 2, (byte)'Z');
            hostInode.SetPageDirty(0);
            hostInode.Mapping.MarkDirty(0);

            Assert.Equal("aXYde", File.ReadAllText(hostFile));
            Assert.Contains(hostInode, hostInode.SuperBlock.Inodes);
            hostInode.RefCount = 0;

            var evicted = VfsShrinker.EvictUnusedInodes(hostInode.SuperBlock);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));
            Assert.Equal(1, evicted);
            Assert.True(hostInode.IsCacheEvicted);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExternalHostShrinkAfterReclaim_MustNotRefaultZeroExtendedData()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-refault-host-shrink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        LinuxFile? file = null;
        AddressSpace? cache = null;
        try
        {
            var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
            file = OpenHostFile(root, "data.bin", runtime.MemoryContext);
            var inode = Assert.IsType<HostInode>(file.Dentry.Inode);
            cache = inode.AcquireMappingRef();

            var warm = new byte[5];
            Assert.Equal(5, inode.ReadToHost(null, file, warm, 0));
            Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(warm));
            Assert.NotEqual(IntPtr.Zero, cache.GetPage(0));

            File.WriteAllText(hostFile, "he");
            Assert.Equal(2UL, inode.Size);

            var reclaimed = runtime.MemoryContext.AddressSpacePolicy.TryReclaimBytes(LinuxConstants.PageSize);
            Assert.True(reclaimed >= LinuxConstants.PageSize);
            Assert.Equal(0, cache.PageCount);

            var readBack = new byte[5];
            var n = inode.ReadToHost(null, file, readBack, 0);
            Assert.Equal(2, n);
            Assert.Equal("he", System.Text.Encoding.ASCII.GetString(readBack, 0, n));
        }
        finally
        {
            if (file?.Dentry.Inode != null) file.Dentry.Inode.Release(file);
            cache?.Release();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mmap_HoldsFileReference_AfterFdCloseUntilMunmap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-mmap-file-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);
            var inode = loc.Dentry.Inode!;
            var refBeforeMmap = inode.RefCount;

            const uint mapAddr = 0x46000000;
            var mmapRc = await CallSys(
                sm,
                engine,
                "SysMmap2",
                mapAddr,
                LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Shared | MapFlags.Fixed),
                (uint)fd);
            Assert.Equal((int)mapAddr, mmapRc);
            Assert.Equal(refBeforeMmap + 1, inode.RefCount);

            sm.FreeFD(fd);
            Assert.Equal(refBeforeMmap, inode.RefCount);

            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);
            Assert.Equal(refBeforeMmap - 1, inode.RefCount);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static LinuxFile OpenHostFile(string rootDir, string relativePath, MemoryRuntimeContext? memoryContext = null)
    {
        var fsType = new FileSystemType { Name = "hostfs" };
        var opts = HostfsMountOptions.Parse("rw");
        var sb = new HostSuperBlock(fsType, rootDir, opts, memoryContext: memoryContext);
        sb.Root = sb.GetDentry(rootDir, FsName.Empty, null)!;
        var dentry = sb.Root.Inode!.Lookup(relativePath);
        Assert.NotNull(dentry);
        var file = new LinuxFile(dentry!, FileFlags.O_RDWR, null!);
        dentry!.Inode!.Open(file);
        return file;
    }

    private static async ValueTask<int> CallSys(SyscallManager sm, Engine engine, string methodName, uint a1 = 0,
        uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }

    private static void StartScheduledAction<T>(Func<ValueTask<T>> action, TaskCompletionSource<T> tcs)
    {
        TraceAsyncFsyncTest($"StartScheduledAction begin thread={Environment.CurrentManagedThreadId}");
        ValueTask<T> pending;
        try
        {
            pending = action();
        }
        catch (Exception ex)
        {
            TraceAsyncFsyncTest($"StartScheduledAction sync-throw {ex.GetType().Name}");
            tcs.TrySetException(ex);
            return;
        }

        if (pending.IsCompletedSuccessfully)
        {
            TraceAsyncFsyncTest("StartScheduledAction completed synchronously");
            tcs.TrySetResult(pending.Result);
            return;
        }

        TraceAsyncFsyncTest("StartScheduledAction awaiting asynchronously");
        _ = CompleteScheduledActionAsync(pending, tcs);
    }

    private static async Task CompleteScheduledActionAsync<T>(ValueTask<T> pending, TaskCompletionSource<T> tcs)
    {
        try
        {
            TraceAsyncFsyncTest($"CompleteScheduledActionAsync awaiting thread={Environment.CurrentManagedThreadId}");
            tcs.TrySetResult(await pending);
            TraceAsyncFsyncTest("CompleteScheduledActionAsync completed");
        }
        catch (Exception ex)
        {
            TraceAsyncFsyncTest($"CompleteScheduledActionAsync throw {ex.GetType().Name}");
            tcs.TrySetException(ex);
        }
    }

    private static bool WaitForSchedulerReady(KernelScheduler scheduler, FiberTask task, TimeSpan timeout)
    {
        TraceAsyncFsyncTest("WaitForSchedulerReady enqueue");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.ScheduleFromAnyThread(() =>
        {
            TraceAsyncFsyncTest($"WaitForSchedulerReady callback thread={Environment.CurrentManagedThreadId}");
            ready.TrySetResult();
        }, task);
        var ok = ready.Task.Wait(timeout);
        TraceAsyncFsyncTest($"WaitForSchedulerReady done ok={ok}");
        return ok;
    }

    private static async Task StopSchedulerAsync(KernelScheduler scheduler, FiberTask task, Task schedulerThread)
    {
        TraceAsyncFsyncTest("StopSchedulerAsync enqueue");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.ScheduleFromAnyThread(() =>
        {
            TraceAsyncFsyncTest($"StopSchedulerAsync callback thread={Environment.CurrentManagedThreadId}");
            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
            scheduler.Running = false;
            stopped.TrySetResult();
        }, task);
        await stopped.Task;
        TraceAsyncFsyncTest("StopSchedulerAsync waiting scheduler thread");
        await schedulerThread;
        TraceAsyncFsyncTest("StopSchedulerAsync completed");
    }

    private static void TraceAsyncFsyncTest(string message)
    {
        if (!AsyncFsyncDebugEnabled)
            return;

        Console.Error.WriteLine($"[HostfsAsyncFsyncTest {DateTime.UtcNow:O}] {message}");
    }
}
