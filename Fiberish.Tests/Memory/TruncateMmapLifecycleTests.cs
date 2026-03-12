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

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize));
        env.Mm.OnFileTruncate(env.File.Inode, LinuxConstants.PageSize, env.Engine);

        Assert.Equal(FaultResult.BusError, env.Mm.HandleFaultDetailed(secondPage, true, env.Engine));
    }

    [Fact]
    public void PrivateMapping_PageBeyondNewEof_FaultsAsBusError()
    {
        using var env = new TestEnv();
        var mapped = env.MapPrivate(0x40400000, LinuxConstants.PageSize * 2);
        Assert.Equal((uint)0x40400000, mapped);

        var secondPage = mapped + LinuxConstants.PageSize;
        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize));
        env.Mm.OnFileTruncate(env.File.Inode, LinuxConstants.PageSize, env.Engine);

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

        var vma = Assert.Single(env.Mm.VMAs);
        var secondPageIndex = vma.GetPageIndex(secondPage);
        Assert.NotEqual(IntPtr.Zero, vma.VmMapping.GetPage(secondPageIndex));

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize));
        env.Mm.OnFileTruncate(env.File.Inode, LinuxConstants.PageSize, env.Engine);

        Assert.Equal(LinuxConstants.PageSize, vma.GetFileBackingLength());
        Assert.Equal(IntPtr.Zero, vma.VmMapping.GetPage(secondPageIndex));
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
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Mm1, env.Engine1, env.Inode, LinuxConstants.PageSize,
            env.Process1);

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
        Assert.True(env.Mm1.ExternalPages.TryGet(second1, out _));
        Assert.True(env.Mm2.ExternalPages.TryGet(second2, out _));

        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Mm1, env.Engine1, env.Inode, LinuxConstants.PageSize,
            env.Process1);

        Assert.False(env.Mm1.ExternalPages.TryGet(second1, out _));
        Assert.False(env.Mm2.ExternalPages.TryGet(second2, out _));
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
        Assert.True(env.Mm.ExternalPages.TryGet(secondPage, out _));

        Assert.Equal(0, env.File.Inode.Truncate(LinuxConstants.PageSize));
        env.Mm.OnFileTruncate(env.File.Inode, LinuxConstants.PageSize, env.Engine);

        Assert.False(env.Mm.ExternalPages.TryGet(secondPage, out _));
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
        vma.VmMapping.MarkDirty(secondPageIndex);
        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        Assert.Equal(0UL, env.Task1.PendingSignals);

        VMAManager.SyncVMA(vma, [env.Engine1], vma.Start, vma.End);

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

        KernelScheduler.Current = null;

        Assert.Equal(0, env.Inode.Truncate(LinuxConstants.PageSize));
        ProcessAddressSpaceSync.NotifyInodeTruncated(env.Mm1, env.Engine1, env.Inode, LinuxConstants.PageSize,
            env.Process1);

        Assert.Equal(FaultResult.BusError,
            env.Mm2.HandleFaultDetailed(env.Map2 + LinuxConstants.PageSize, true, env.Engine2));
    }

    [Fact]
    public async Task NotifyInodeTruncated_SharedAddressSpacePeerEngine_UsesSequenceInvalidation()
    {
        var scheduler = new KernelScheduler();
        KernelScheduler.Current = scheduler;
        try
        {
            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "truncate-mm-shared", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry("data.bin", null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            var inode = dentry.Inode!;

            using var engine = new Engine();
            var mm = new VMAManager();
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
                Assert.Equal(payload.Length, inode.Write(file, payload, 0));

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
                    ProcessAddressSpaceSync.NotifyInodeTruncated(mm, engine, inode, LinuxConstants.PageSize, process);

                    Assert.True(peerEngine.AddressSpaceMapSequenceSeen < mm.CurrentMapSequence);

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
        finally
        {
            KernelScheduler.Current = null;
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

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Mm = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Mm.HandleFault(addr, isWrite, Engine);

            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "truncate-mm", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry("data.bin", null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            var file = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            File = (file, dentry.Inode!);

            var payload = new byte[LinuxConstants.PageSize * 2];
            payload.AsSpan(0, 4).Fill((byte)'A');
            payload.AsSpan(LinuxConstants.PageSize, 4).Fill((byte)'B');
            Assert.Equal(payload.Length, dentry.Inode!.Write(file, payload, 0));
        }

        public Engine Engine { get; }
        public VMAManager Mm { get; }
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
            KernelScheduler.Current = Scheduler;

            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "truncate-mm-multi", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry("data.bin", null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            Inode = dentry.Inode!;

            Engine1 = new Engine();
            Engine2 = new Engine();
            Mm1 = new VMAManager();
            Mm2 = new VMAManager();
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
            Assert.Equal(payload.Length, Inode.Write(File1, payload, 0));

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
            KernelScheduler.Current = null;
        }
    }

    private sealed class MixedMappingEnv : IDisposable
    {
        public MixedMappingEnv(MapFlags flags1, MapFlags flags2)
        {
            Scheduler = new KernelScheduler();
            KernelScheduler.Current = Scheduler;

            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "mixed-mm", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry("data.bin", null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            Inode = dentry.Inode!;

            Engine1 = new Engine();
            Engine2 = new Engine();
            Mm1 = new VMAManager();
            Mm2 = new VMAManager();
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
            Assert.Equal(1, Inode.Write(File1, new[] { (byte)'A' }, 0));

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
            KernelScheduler.Current = null;
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