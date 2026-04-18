using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;
using LinuxFile = Fiberish.VFS.LinuxFile;

namespace Fiberish.Tests.Memory;

public class TruncateMmapLifecycleTests
{
    [Fact]
    public void SharedMapping_PageBeyondNewEof_FaultsAsBusError()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x40000000, LinuxConstants.PageSize * 2);
        Assert.Equal((uint)0x40000000, mapped);

        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, true, env.Engine));

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize, new FileMutationContext(env.Mm, env.Engine)));

        Assert.Equal(FaultResult.BusError, env.Mm.HandleFaultDetailed(secondPage, true, env.Engine));
    }

    [Fact]
    public void PrivateMapping_PageBeyondNewEof_FaultsAsBusError()
    {
        using var env = new TestEnv();
        var mapped = env.MapPrivate(0x40400000, LinuxConstants.PageSize * 2);
        Assert.Equal((uint)0x40400000, mapped);

        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize, new FileMutationContext(env.Mm, env.Engine)));

        Assert.Equal(FaultResult.BusError, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.Equal(FaultResult.BusError, env.Mm.HandleFaultDetailed(secondPage, true, env.Engine));
    }

    [Fact]
    public void TruncateNotification_UpdatesBackingLength_AndDropsBeyondEofPages()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41000000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));

        var vma = Assert.Single(env.Mm.VMAs.Where(candidate => candidate.Start == mapped));
        var secondPageIndex = vma.GetPageIndex(secondPage);
        Assert.NotNull(vma.VmMapping);
        Assert.NotEqual(IntPtr.Zero, vma.VmMapping.GetPage(secondPageIndex));

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize, new FileMutationContext(env.Mm, env.Engine)));

        Assert.Equal(LinuxConstants.PageSize, vma.GetFileBackingLength());
        Assert.Equal(IntPtr.Zero, vma.VmMapping.GetPage(secondPageIndex));
    }

    [Fact]
    public void ExplicitTruncate_SharedMapping_PreservesResidentTailPage_AndZerosTrimmedBytes()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41200000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;

        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.True(env.Engine.CopyToUser(secondPage + 200, [(byte)'Z']));

        Assert.Equal(0,
            env.File.Inode.Truncate(LinuxConstants.PageSize + 123, new FileMutationContext(env.Mm, env.Engine)));

        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));
        var prefix = new byte[1];
        var tail = new byte[1];
        Assert.True(env.Engine.CopyFromUser(secondPage, prefix));
        Assert.True(env.Engine.CopyFromUser(secondPage + 200, tail));
        Assert.Equal((byte)'B', prefix[0]);
        Assert.Equal((byte)0, tail[0]);
    }

    [Fact]
    public void ExplicitTruncate_PrivateCowTailPage_PreservesPrefix_AndZerosTrimmedBytes()
    {
        using var env = new TestEnv();
        var mapped = env.MapPrivate(0x41400000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;

        Assert.True(env.Engine.CopyToUser(secondPage, [(byte)'P']));
        Assert.True(env.Engine.CopyToUser(secondPage + 200, [(byte)'Q']));
        var before = new byte[1];
        Assert.True(env.Engine.CopyFromUser(secondPage, before));
        Assert.Equal((byte)'P', before[0]);

        Assert.Equal(0,
            env.File.Inode.Truncate(LinuxConstants.PageSize + 123, new FileMutationContext(env.Mm, env.Engine)));

        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));
        var prefix = new byte[1];
        var tail = new byte[1];
        Assert.True(env.Engine.CopyFromUser(secondPage, prefix));
        Assert.True(env.Engine.CopyFromUser(secondPage + 200, tail));
        Assert.Equal((byte)'P', prefix[0]);
        Assert.Equal((byte)0, tail[0]);
    }

    [Fact]
    public void ExplicitTruncate_PrivateCowTailPage_FromHostMappedSource_PreservesPrivatePrefix_AndZerosTrimmedBytes()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"truncate-private-host-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "tail.bin");
        var payload = new byte[LinuxConstants.PageSize + 123];
        payload.AsSpan(0, LinuxConstants.PageSize).Fill((byte)'A');
        payload.AsSpan(LinuxConstants.PageSize, 123).Fill((byte)'B');
        File.WriteAllBytes(hostFile, payload);

        try
        {
            var runtime = new TestRuntimeFactory();
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/tail.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            using var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            const uint mapAddr = 0x41500000;
            var mapped = mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, file, 0, "private-host-tail", engine);
            var secondPage = mapped + LinuxConstants.PageSize;

            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(secondPage, false, engine));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            Assert.Equal(1, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);

            Assert.True(engine.CopyToUser(secondPage, [(byte)'P']));
            Assert.True(engine.CopyToUser(secondPage + 16, [(byte)'Q']));
            Assert.True(engine.CopyToUser(secondPage + 64, [(byte)'R']));

            var vma = Assert.Single(mm.VMAs.Where(candidate => candidate.Start == mapped));
            var secondPageIndex = vma.GetPageIndex(secondPage);
            Assert.NotNull(vma.VmAnonVma);
            var privatePageBefore = vma.VmAnonVma!.GetPage(secondPageIndex);
            Assert.NotEqual(IntPtr.Zero, privatePageBefore);

            Assert.Equal(0,
                loc.Dentry!.Inode!.Truncate(LinuxConstants.PageSize + 17, new FileMutationContext(mm, engine)));

            Assert.Equal(0, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);
            Assert.True(mm.PageMapping.TryGet(secondPage, out _));

            var prefix0 = new byte[1];
            var prefix16 = new byte[1];
            var tail64 = new byte[1];
            Assert.True(engine.CopyFromUser(secondPage, prefix0));
            Assert.True(engine.CopyFromUser(secondPage + 16, prefix16));
            Assert.True(engine.CopyFromUser(secondPage + 64, tail64));
            Assert.Equal((byte)'P', prefix0[0]);
            Assert.Equal((byte)'Q', prefix16[0]);
            Assert.Equal((byte)0, tail64[0]);

            Assert.NotNull(vma.VmAnonVma);
            Assert.Equal(privatePageBefore, vma.VmAnonVma!.GetPage(secondPageIndex));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExplicitTruncate_UsesExplicitContextWhenEngineRegistryIsMissing()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41500000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;

        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        env.Syscalls.UnregisterEngine(env.Engine);

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize, new FileMutationContext(env.Mm, env.Engine)));

        Assert.False(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.Equal(FaultResult.BusError, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
    }

    [Fact]
    public void NotifyInodeTruncated_BroadcastsToOtherProcesses_SharedInodeMappings()
    {
        using var env = new MultiProcessEnv();
        Assert.Equal(FaultResult.Handled,
            env.Mm1.HandleFaultDetailed(env.Map1 + LinuxConstants.PageSize, true, env.Engine1));
        Assert.Equal(FaultResult.Handled,
            env.Mm2.HandleFaultDetailed(env.Map2 + LinuxConstants.PageSize, true, env.Engine2));

        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Inode, LinuxConstants.PageSize,
            new FileMutationContext(env.Mm1, env.Engine1, env.Process1));

        Assert.Equal(FaultResult.BusError,
            env.Mm2.HandleFaultDetailed(env.Map2 + LinuxConstants.PageSize, true, env.Engine2));
    }

    [Fact]
    public void NotifyInodeTruncated_BroadcastsInvalidateBeyondEofAcrossAllProcesses()
    {
        using var env = new MultiProcessEnv();
        var second1 = env.Map1 + LinuxConstants.PageSize;
        var second2 = env.Map2 + LinuxConstants.PageSize;

        Assert.Equal(FaultResult.Handled, env.Mm1.HandleFaultDetailed(second1, true, env.Engine1));
        Assert.Equal(FaultResult.Handled, env.Mm2.HandleFaultDetailed(second2, true, env.Engine2));
        Assert.True(env.Mm1.PageMapping.TryGet(second1, out _));
        Assert.True(env.Mm2.PageMapping.TryGet(second2, out _));

        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Inode, LinuxConstants.PageSize,
            new FileMutationContext(env.Mm1, env.Engine1, env.Process1));

        Assert.False(env.Mm1.PageMapping.TryGet(second1, out _));
        Assert.False(env.Mm2.PageMapping.TryGet(second2, out _));
        Assert.Equal(FaultResult.BusError, env.Mm1.HandleFaultDetailed(second1, true, env.Engine1));
        Assert.Equal(FaultResult.BusError, env.Mm2.HandleFaultDetailed(second2, true, env.Engine2));
    }

    [Fact]
    public void PrefaultThenTruncate_RealAccessPath_ReportsBusError()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x44000000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;
        var scratch = new byte[1];

        Assert.True(env.Engine.CopyFromUser(secondPage, scratch));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize, new FileMutationContext(env.Mm, env.Engine)));

        Assert.False(env.Mm.PageMapping.TryGet(secondPage, out _));
        FaultResult? lastFault = null;
        env.Engine.PageFaultResolver = (addr, isWrite) =>
        {
            lastFault = env.Mm.HandleFaultDetailed(addr, isWrite, env.Engine);
            return lastFault == FaultResult.Handled;
        };

        Assert.False(env.Engine.CopyFromUser(secondPage, scratch));
        Assert.Equal(FaultResult.BusError, lastFault);
    }

    [Fact]
    public void SyncVma_BeyondBackingLength_DoesNotPostAsyncSigbus()
    {
        using var env = new MultiProcessEnv();
        var secondPage = env.Map1 + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm1.HandleFaultDetailed(secondPage, true, env.Engine1));

        var vma = Assert.Single(env.Mm1.VMAs, candidate => ReferenceEquals(candidate.File?.OpenedInode, env.Inode));
        var secondPageIndex = vma.GetPageIndex(secondPage);
        Assert.NotNull(vma.VmMapping);
        vma.VmMapping.MarkDirty(secondPageIndex);
        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        Assert.Equal(0UL, env.Task1.PendingSignals);

        VMAManager.SyncVmArea(vma, [env.Engine1], vma.Start, vma.End);

        Assert.Equal(0UL, env.Task1.PendingSignals);
    }

    [Fact]
    public void NotifyInodeTruncated_UsesInodeIndex_WhenSchedulerUnavailable()
    {
        using var env = new MultiProcessEnv();
        Assert.Equal(FaultResult.Handled,
            env.Mm1.HandleFaultDetailed(env.Map1 + LinuxConstants.PageSize, true, env.Engine1));
        Assert.Equal(FaultResult.Handled,
            env.Mm2.HandleFaultDetailed(env.Map2 + LinuxConstants.PageSize, true, env.Engine2));


        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Inode, LinuxConstants.PageSize,
            new FileMutationContext(env.Mm1, env.Engine1, env.Process1));

        Assert.Equal(FaultResult.BusError,
            env.Mm2.HandleFaultDetailed(env.Map2 + LinuxConstants.PageSize, true, env.Engine2));
    }

    [Fact]
    public async Task NotifyInodeTruncated_SharedAddressSpacePeerEngine_UsesSequenceInvalidation()
    {
        var scheduler = new KernelScheduler();
        var runtime = new TestRuntimeFactory();

        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(fsType, 0, "truncate-mm-shared", null);
        var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        var dentry = new Dentry(FsName.FromString("data.bin"), null, sb.Root, sb);
        sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
        var inode = dentry.Inode!;

        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        var process = new Process(9101, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(9101, process, engine, scheduler);
        engine.Owner = task;
        engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

        var file = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
        try
        {
            var payload = new byte[LinuxConstants.PageSize * 2];
            payload.AsSpan(0, 4).Fill((byte)'A');
            payload.AsSpan(LinuxConstants.PageSize, 4).Fill((byte)'B');
            Assert.Equal(payload.Length, inode.WriteFromHost(null, file, payload, 0));

            var map = mm.Mmap(0x45000000, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared, file, 0, "shared-mm-map", engine);
            var secondPage = map + LinuxConstants.PageSize;
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(secondPage, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            try
            {
                var peerEngine = peer.CPU;
                var probe = new byte[1];
                Assert.True(peerEngine.CopyFromUser(secondPage, probe));

                Assert.Equal(0, inode.Truncate(LinuxConstants.PageSize));
                ProcessAddressSpaceSync.NotifyInodeTruncated(inode, LinuxConstants.PageSize,
                    new FileMutationContext(mm, engine, process));

                Assert.True(peerEngine.AddressSpaceCodeCacheSequenceSeen < mm.CurrentCodeCacheSequence);

                FaultResult? lastFault = null;
                peerEngine.PageFaultResolver = (addr, isWrite) =>
                {
                    lastFault = mm.HandleFaultDetailed(addr, isWrite, peerEngine);
                    return lastFault == FaultResult.Handled;
                };

                Assert.False(peerEngine.CopyFromUser(secondPage, probe));
                Assert.Equal(FaultResult.BusError, lastFault);
            }
            finally
            {
                _ = scheduler.DetachTask(peer);
            }
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void PrivateMapping_SeesSharedWriterUntilItTriggersCow()
    {
        using var env = new MixedMappingEnv(MapFlags.Private, MapFlags.Shared);

        Assert.Equal((byte)'A', env.ReadByte1());
        env.WriteByte2((byte)'S');
        Assert.Equal((byte)'S', env.ReadByte1());

        env.WriteByte1((byte)'P');
        Assert.Equal((byte)'P', env.ReadByte1());

        env.WriteByte2((byte)'T');
        Assert.Equal((byte)'P', env.ReadByte1());
        Assert.Equal((byte)'T', env.ReadByte2());
    }

    [Fact]
    public void PrivateMappings_DoNotObserveEachOthersWrites()
    {
        using var env = new MixedMappingEnv(MapFlags.Private, MapFlags.Private);

        Assert.Equal((byte)'A', env.ReadByte1());
        Assert.Equal((byte)'A', env.ReadByte2());

        env.WriteByte2((byte)'Q');
        Assert.Equal((byte)'A', env.ReadByte1());
        Assert.Equal((byte)'Q', env.ReadByte2());

        env.WriteByte1((byte)'P');
        Assert.Equal((byte)'P', env.ReadByte1());
        Assert.Equal((byte)'Q', env.ReadByte2());
    }

    [Fact]
    public void VfsStreamSetLength_ShrinksMappedFile_AndPreservesPartialTailPage()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41600000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        var streamFile = new LinuxFile(env.File.Handle.Dentry, FileFlags.O_RDWR, env.File.Handle.Mount);
        using var stream = new VfsStream(streamFile, new FileMutationContext(env.Mm, env.Engine));
        stream.SetLength(LinuxConstants.PageSize + 123);

        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
    }

    [Fact]
    public void VfsStreamSetLength_WithoutContext_RejectsLiveMappedShrink()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41700000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        var streamFile = new LinuxFile(env.File.Handle.Dentry, FileFlags.O_RDWR, env.File.Handle.Mount);
        using var stream = new VfsStream(streamFile);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.SetLength(LinuxConstants.PageSize + 123));
        Assert.Contains("FileMutationContext", ex.Message);
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
    }

    [Fact]
    public void OverlayUpperOnlySharedMapping_ExplicitTruncate_InvalidatesBeyondEofPage()
    {
        var runtime = new TestRuntimeFactory();
        var overlaySb = CreateUpperOnlyOverlay(runtime, "data.bin", CreatePayload(LinuxConstants.PageSize * 2));
        var fileDentry = LookupOverlayFile(overlaySb, "/data.bin");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);

        try
        {
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            const uint mapAddr = 0x41900000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "overlay-upper-only", engine);

            var secondPage = mapAddr + LinuxConstants.PageSize;
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(secondPage, false, engine));
            Assert.True(mm.PageMapping.TryGet(secondPage, out _));

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            Assert.NotNull(overlayInode.UpperInode);
            Assert.Same(overlayInode.UpperInode!.Mapping, vma!.VmMapping);

            Assert.Equal(0, overlayInode.Truncate(LinuxConstants.PageSize, new FileMutationContext(mm, engine)));

            Assert.False(mm.PageMapping.TryGet(secondPage, out _));
            Assert.Equal(FaultResult.BusError, mm.HandleFaultDetailed(secondPage, false, engine));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void OverlayLowerOnlyCopyUpThenTruncate_InvalidatesOverlayAndDirectUpperMappings()
    {
        var runtime = new TestRuntimeFactory();
        var overlaySb = CreateLowerOnlyOverlay(runtime, "data.bin", CreatePayload(LinuxConstants.PageSize * 2));
        var fileDentry = LookupOverlayFile(overlaySb, "/data.bin");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var mappedFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        var writerFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        LinuxFile? upperFile = null;

        try
        {
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            const uint overlayAddr = 0x41B00000;
            const uint upperAddr = 0x41D00000;
            mm.Mmap(overlayAddr, LinuxConstants.PageSize * 2, Protection.Read,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "overlay-lower-before-copyup", engine);

            var overlaySecondPage = overlayAddr + LinuxConstants.PageSize;
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(overlaySecondPage, false, engine));
            Assert.True(mm.PageMapping.TryGet(overlaySecondPage, out _));
            Assert.Null(overlayInode.UpperInode);

            Assert.Equal(0, overlayInode.CopyUp(writerFile));
            Assert.NotNull(overlayInode.UpperDentry);
            upperFile = new LinuxFile(overlayInode.UpperDentry!, FileFlags.O_RDWR, null!);

            var overlayVma = mm.FindVmArea(overlayAddr);
            Assert.NotNull(overlayVma);
            Assert.NotNull(overlayInode.UpperInode);
            Assert.Same(overlayInode.UpperInode!.Mapping, overlayVma!.VmMapping);

            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(overlaySecondPage, false, engine));
            Assert.True(mm.PageMapping.TryGet(overlaySecondPage, out _));

            mm.Mmap(upperAddr, LinuxConstants.PageSize * 2, Protection.Read,
                MapFlags.Shared | MapFlags.Fixed, upperFile, 0, "overlay-upper-direct", engine);

            var upperSecondPage = upperAddr + LinuxConstants.PageSize;
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(upperSecondPage, false, engine));
            Assert.True(mm.PageMapping.TryGet(upperSecondPage, out _));

            Assert.Equal(0, overlayInode.Truncate(LinuxConstants.PageSize, new FileMutationContext(mm, engine)));

            Assert.False(mm.PageMapping.TryGet(overlaySecondPage, out _));
            Assert.False(mm.PageMapping.TryGet(upperSecondPage, out _));
            Assert.Equal(FaultResult.BusError, mm.HandleFaultDetailed(overlaySecondPage, false, engine));
            Assert.Equal(FaultResult.BusError, mm.HandleFaultDetailed(upperSecondPage, false, engine));
        }
        finally
        {
            upperFile?.Close();
            writerFile.Close();
            mappedFile.Close();
        }
    }

    [Fact]
    public void WriteGuestFile_RewritesMappedFile_AndTearsDownResidentTailPage()
    {
        using var env = new TestEnv();
        var mapped = env.MapShared(0x41800000, LinuxConstants.PageSize * 2);
        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        env.Syscalls.WriteGuestFile("/data.bin", CreatePayload(LinuxConstants.PageSize + 123));

        Assert.False(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
    }

    [Fact]
    public void WriteFileInDetachedMount_RewritesMappedFile_AndTearsDownResidentTailPage()
    {
        using var env = new TestEnv();
        var detached = env.Syscalls.CreateDetachedTmpfsMount("truncate-detached");
        env.Syscalls.WriteFileInDetachedMount(detached, "data.bin", CreatePayload(LinuxConstants.PageSize * 2));
        env.Syscalls.BindMountSubtree(detached, "", "/detached");

        var loc = env.Syscalls.PathWalkWithFlags("/detached/data.bin", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);

        using var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
        const uint mapAddr = 0x41A00000;
        var mapped = env.Mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
            MapFlags.Shared, file, 0, "detached-map", env.Engine);
        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
        Assert.True(env.Mm.PageMapping.TryGet(secondPage, out _));

        env.Syscalls.WriteFileInDetachedMount(detached, "data.bin", CreatePayload(LinuxConstants.PageSize + 123));

        Assert.False(env.Mm.PageMapping.TryGet(secondPage, out _));
        Assert.Equal(FaultResult.Handled, env.Mm.HandleFaultDetailed(secondPage, false, env.Engine));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        if (length > 0)
            payload[0] = (byte)'A';
        if (length > LinuxConstants.PageSize)
            payload[LinuxConstants.PageSize] = (byte)'B';
        return payload;
    }

    private static OverlaySuperBlock CreateUpperOnlyOverlay(TestRuntimeFactory runtime, string fileName, byte[] payload)
    {
        var tmpType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
        };
        var lowerSb = tmpType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "truncate-overlay-lower-empty", null);
        var upperSb = tmpType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "truncate-overlay-upper", null);
        var upperFileDentry = new Dentry(FsName.FromString(fileName), null, upperSb.Root, upperSb);
        upperSb.Root.Inode!.Create(upperFileDentry, 0x1A4, 0, 0);
        var upperFile = new LinuxFile(upperFileDentry, FileFlags.O_RDWR, null!);
        try
        {
            Assert.Equal(payload.Length, upperFileDentry.Inode!.WriteFromHost(null, upperFile, payload, 0));
        }
        finally
        {
            upperFile.Close();
        }

        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "truncate-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private static OverlaySuperBlock CreateLowerOnlyOverlay(TestRuntimeFactory runtime, string fileName, byte[] payload)
    {
        var tmpType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
        };
        var lowerSb = tmpType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "truncate-overlay-lower", null);
        var lowerFileDentry = new Dentry(FsName.FromString(fileName), null, lowerSb.Root, lowerSb);
        lowerSb.Root.Inode!.Create(lowerFileDentry, 0x1A4, 0, 0);
        var lowerFile = new LinuxFile(lowerFileDentry, FileFlags.O_RDWR, null!);
        try
        {
            Assert.Equal(payload.Length, lowerFileDentry.Inode!.WriteFromHost(null, lowerFile, payload, 0));
        }
        finally
        {
            lowerFile.Close();
        }

        var upperSb = tmpType.CreateAnonymousFileSystem(runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "truncate-overlay-upper-empty", null);

        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "truncate-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private static Dentry LookupOverlayFile(OverlaySuperBlock overlaySb, string path)
    {
        var current = overlaySb.Root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Inode!.Lookup(segment);
            Assert.NotNull(current);
        }

        return current!;
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            var runtime = new TestRuntimeFactory();
            Engine = runtime.CreateEngine();
            Mm = runtime.CreateAddressSpace();
            Engine.PageFaultResolver = (addr, isWrite) => Mm.HandleFault(addr, isWrite, Engine);
            Syscalls = new SyscallManager(Engine, Mm, 0);

            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var sb = fsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(fsType, 0, "truncate-mm", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            Syscalls.InitializeRoot(sb.Root, mount);
            var dentry = new Dentry(FsName.FromString("data.bin"), null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            var file = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            File = (file, dentry.Inode!);

            var payload = new byte[LinuxConstants.PageSize * 2];
            payload.AsSpan(0, 4).Fill((byte)'A');
            payload.AsSpan(LinuxConstants.PageSize, 4).Fill((byte)'B');
            Assert.Equal(payload.Length, dentry.Inode!.WriteFromHost(null, file, payload, 0));
        }

        public Engine Engine { get; }
        public VMAManager Mm { get; }
        public SyscallManager Syscalls { get; }
        public (LinuxFile Handle, Fiberish.VFS.Inode Inode) File { get; }

        public void Dispose()
        {
            File.Handle.Close();
            Engine.Dispose();
        }

        public uint MapShared(uint addr, uint len)
        {
            return Mm.Mmap(addr, len, Protection.Read | Protection.Write, MapFlags.Shared, File.Handle, 0, "test-map",
                Engine);
        }

        public uint MapPrivate(uint addr, uint len)
        {
            return Mm.Mmap(addr, len, Protection.Read | Protection.Write, MapFlags.Private, File.Handle, 0,
                "test-map-private", Engine);
        }
    }

    private sealed class MultiProcessEnv : IDisposable
    {
        public MultiProcessEnv()
        {
            Scheduler = new KernelScheduler();
            var runtime = new TestRuntimeFactory();

            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var sb = fsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(fsType, 0, "truncate-mm-multi", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry(FsName.FromString("data.bin"), null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            Inode = dentry.Inode!;

            Engine1 = runtime.CreateEngine();
            Engine2 = runtime.CreateEngine();
            Mm1 = runtime.CreateAddressSpace();
            Mm2 = runtime.CreateAddressSpace();
            Sm1 = new SyscallManager(Engine1, Mm1, 0);
            Sm2 = new SyscallManager(Engine2, Mm2, 0);
            Process1 = new Process(9001, Mm1, Sm1);
            Process2 = new Process(9002, Mm2, Sm2);
            Scheduler.RegisterProcess(Process1);
            Scheduler.RegisterProcess(Process2);

            Task1 = new FiberTask(9001, Process1, Engine1, Scheduler);
            Task2 = new FiberTask(9002, Process2, Engine2, Scheduler);
            Engine1.Owner = Task1;
            Engine2.Owner = Task2;
            Engine1.PageFaultResolver = (addr, isWrite) => Mm1.HandleFault(addr, isWrite, Engine1);
            Engine2.PageFaultResolver = (addr, isWrite) => Mm2.HandleFault(addr, isWrite, Engine2);

            File1 = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            File2 = new LinuxFile(dentry, FileFlags.O_RDWR, mount);

            var payload = new byte[LinuxConstants.PageSize * 2];
            payload.AsSpan(0, 4).Fill((byte)'A');
            payload.AsSpan(LinuxConstants.PageSize, 4).Fill((byte)'B');
            Assert.Equal(payload.Length, Inode.WriteFromHost(null, File1, payload, 0));

            Map1 = Mm1.Mmap(0x42000000, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared, File1, 0, "p1-map", Engine1);
            Map2 = Mm2.Mmap(0x43000000, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared, File2, 0, "p2-map", Engine2);
        }

        public KernelScheduler Scheduler { get; }
        public Engine Engine1 { get; }
        public Engine Engine2 { get; }
        public VMAManager Mm1 { get; }
        public VMAManager Mm2 { get; }
        public SyscallManager Sm1 { get; }
        public SyscallManager Sm2 { get; }
        public Process Process1 { get; }
        public Process Process2 { get; }
        public FiberTask Task1 { get; }
        public FiberTask Task2 { get; }
        public LinuxFile File1 { get; }
        public LinuxFile File2 { get; }
        public Inode Inode { get; }
        public uint Map1 { get; }
        public uint Map2 { get; }

        public void Dispose()
        {
            File1.Close();
            File2.Close();
            Engine1.Dispose();
            Engine2.Dispose();
        }
    }

    private sealed class MixedMappingEnv : IDisposable
    {
        public MixedMappingEnv(MapFlags flags1, MapFlags flags2)
        {
            Scheduler = new KernelScheduler();
            var runtime = new TestRuntimeFactory();

            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var sb = fsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(fsType, 0, "mixed-mm", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry(FsName.FromString("data.bin"), null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            Inode = dentry.Inode!;

            Engine1 = runtime.CreateEngine();
            Engine2 = runtime.CreateEngine();
            Mm1 = runtime.CreateAddressSpace();
            Mm2 = runtime.CreateAddressSpace();
            Sm1 = new SyscallManager(Engine1, Mm1, 0);
            Sm2 = new SyscallManager(Engine2, Mm2, 0);
            Process1 = new Process(9101, Mm1, Sm1);
            Process2 = new Process(9102, Mm2, Sm2);
            Scheduler.RegisterProcess(Process1);
            Scheduler.RegisterProcess(Process2);

            Task1 = new FiberTask(9101, Process1, Engine1, Scheduler);
            Task2 = new FiberTask(9102, Process2, Engine2, Scheduler);
            Engine1.Owner = Task1;
            Engine2.Owner = Task2;
            Engine1.PageFaultResolver = (addr, isWrite) => Mm1.HandleFault(addr, isWrite, Engine1);
            Engine2.PageFaultResolver = (addr, isWrite) => Mm2.HandleFault(addr, isWrite, Engine2);

            File1 = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            File2 = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            Assert.Equal(1, Inode.WriteFromHost(null, File1, new[] { (byte)'A' }, 0));

            Map1 = Mm1.Mmap(0x46000000, LinuxConstants.PageSize, Protection.Read | Protection.Write, flags1, File1, 0,
                "map1", Engine1);
            Map2 = Mm2.Mmap(0x46100000, LinuxConstants.PageSize, Protection.Read | Protection.Write, flags2, File2, 0,
                "map2", Engine2);
        }

        public KernelScheduler Scheduler { get; }
        public Engine Engine1 { get; }
        public Engine Engine2 { get; }
        public VMAManager Mm1 { get; }
        public VMAManager Mm2 { get; }
        public SyscallManager Sm1 { get; }
        public SyscallManager Sm2 { get; }
        public Process Process1 { get; }
        public Process Process2 { get; }
        public FiberTask Task1 { get; }
        public FiberTask Task2 { get; }
        public LinuxFile File1 { get; }
        public LinuxFile File2 { get; }
        public Inode Inode { get; }
        public uint Map1 { get; }
        public uint Map2 { get; }

        public void Dispose()
        {
            File1.Close();
            File2.Close();
            Engine1.Dispose();
            Engine2.Dispose();
        }

        public byte ReadByte1()
        {
            return ReadByte(Engine1, Map1);
        }

        public byte ReadByte2()
        {
            return ReadByte(Engine2, Map2);
        }

        public void WriteByte1(byte value)
        {
            WriteByte(Engine1, Map1, value);
        }

        public void WriteByte2(byte value)
        {
            WriteByte(Engine2, Map2, value);
        }

        private static byte ReadByte(Engine engine, uint addr)
        {
            var buffer = new byte[1];
            Assert.True(engine.CopyFromUser(addr, buffer));
            return buffer[0];
        }

        private static void WriteByte(Engine engine, uint addr, byte value)
        {
            Assert.True(engine.CopyToUser(addr, new[] { value }));
        }
    }
}
