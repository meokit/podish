using System.IO.MemoryMappedFiles;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public sealed class MappedFileBackendContractTests
{
    public static TheoryData<string, int, long> DirectPageCases => new()
    {
        { "hostfs", 1, 0 }, { "silkfs", 1, 0 },
        { "hostfs", 4095, 0 }, { "silkfs", 4095, 0 },
        { "hostfs", 4096, 0 }, { "silkfs", 4096, 0 },
        { "hostfs", 4097, 4096 }, { "silkfs", 4097, 4096 },
        { "hostfs", 2 * 1024 * 1024, 2 * 1024 * 1024 - 4096 },
        { "silkfs", 2 * 1024 * 1024, 2 * 1024 * 1024 - 4096 },
        { "hostfs", 2 * 1024 * 1024 + 1, 2 * 1024 * 1024 },
        { "silkfs", 2 * 1024 * 1024 + 1, 2 * 1024 * 1024 }
    };

    [Theory]
    [MemberData(nameof(DirectPageCases))]
    public void SharedFault_UsesLeasedHostViewWithoutShadowPage(string backend, int length, long offset)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, length);
        env.Map(offset);
        var before = env.TotalPooledAllocations;
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        Assert.Equal(before, env.TotalPooledAllocations);
        var index = (uint)(offset / LinuxConstants.PageSize);
        Assert.True(env.Inode.TryGetMappingPageRecord(index, out var record));
        Assert.Equal(FilePageBackingKind.HostMappedWindow, record.BackingKind);
        Assert.True(env.Memory.PageMapping.TryGetBinding(MappedFileBackendTestEnvironment.Address, out var binding));
        Assert.Equal(record.Ptr, binding!.Value.Ptr);
        var provider = Assert.IsAssignableFrom<IHostMappedCacheDropper>(env.Inode);
        Assert.True(provider.TryDescribeMappedPage(index, binding.Value.Ptr, out var lease));
        Assert.True(lease.WindowId > 0);
        Assert.False(lease.Retired);
        Assert.True(lease.WindowRefCount > 0);
        Assert.InRange(binding.Value.Ptr.ToInt64() - lease.WindowPointer.ToInt64(), 0, lease.WindowLength - 4096);
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(index), env.Read(MappedFileBackendTestEnvironment.Address));

        // Independent native view sees the write before any guest msync/fsync.
        using var file = new FileStream(env.BackingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var mapped = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, false);
        using var view = mapped.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        Assert.True(env.Engine.CopyToUser(MappedFileBackendTestEnvironment.Address, new byte[] { 0xA7 }));
        Assert.Equal((byte)0xA7, view.ReadByte(offset));
        view.Write(offset, (byte)0x5B);
        Assert.Equal((byte)0x5B, env.Read(MappedFileBackendTestEnvironment.Address));
        Assert.Equal(before, env.TotalPooledAllocations);
        Assert.Equal(length, file.Length);
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void SharedAliases_AcrossAddressSpacesObserveWritesWithoutFlush(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 8192);
        using var peer = new Engine(env.Context);
        var peerMemory = new VMAManager(env.Context);
        env.Map(4096);
        env.Map(4096, MappedFileBackendTestEnvironment.Address + 8192);
        env.File.Get();
        try
        {
            peerMemory.Mmap(0x61000000, 4096, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, env.File, 4096, "peer", peer);
            Assert.Equal((byte)2, env.Read(MappedFileBackendTestEnvironment.Address));
            Assert.True(peerMemory.HandleFault(0x61000000, false, peer));
            Assert.True(peer.CopyToUser(0x61000000 + 53, new byte[] { 0xCC }));
            Assert.Equal((byte)0xCC, env.Read(MappedFileBackendTestEnvironment.Address + 53));
            Assert.Equal((byte)0xCC, env.Read(MappedFileBackendTestEnvironment.Address + 8192 + 53));
        }
        finally
        {
            peerMemory.Clear(peer);
        }
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void PrivateWrite_AllocatesCowWithoutModifyingSharedSource(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096);
        env.Map(shared: false);
        Assert.Equal((byte)1, env.Read(MappedFileBackendTestEnvironment.Address));
        var before = env.Context.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Cow).AllocatedPages;
        Assert.True(env.Engine.CopyToUser(MappedFileBackendTestEnvironment.Address, new byte[] { 0xAB }));
        Assert.Equal(before + 1, env.Context.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Cow).AllocatedPages);
        env.Map(address: MappedFileBackendTestEnvironment.Address + 8192);
        Assert.Equal((byte)1, env.Read(MappedFileBackendTestEnvironment.Address + 8192));
        Assert.Equal((byte)0xAB, env.Read(MappedFileBackendTestEnvironment.Address));
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void EmptyFile_FaultsSigbusInsteadOfCreatingAZeroPage(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 0);
        env.Map();
        var before = env.TotalPooledAllocations;
        Assert.Equal(FaultResult.BusError, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        Assert.Equal(before, env.TotalPooledAllocations);
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void ForcedBufferedFallback_IsNotMisreportedAsDirectMapping(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096, buffered: true);
        env.Map();
        var before = env.TotalPooledAllocations;
        Assert.Equal((byte)1, env.Read(MappedFileBackendTestEnvironment.Address));
        Assert.True(env.TotalPooledAllocations > before);
        Assert.True(env.Inode.TryGetMappingPageRecord(0, out var record));
        Assert.Equal(FilePageBackingKind.AllocatedPageCache, record.BackingKind);
        Assert.False(((IHostMappedCacheDropper)env.Inode).TryDescribeMappedPage(0, record.Ptr, out _));
    }

    [Fact]
    public void LeaseDiagnostics_RejectForeignPointersAndTrackRetirementUntilRelease()
    {
        using var env = new MappedFileBackendTestEnvironment("hostfs", 4096);
        using var cache = new MappedFilePageCache(env.BackingPath);
        Assert.True(cache.TryAcquirePageLease(0, 4096, false, out var pointer, out var token));
        Assert.True(cache.TryDescribePagePointer(0, pointer, out var original));
        Assert.False(cache.TryDescribePagePointer(1, pointer, out _));
        Assert.False(cache.TryDescribePagePointer(0, pointer + 1, out _));
        Assert.False(cache.TryDescribePagePointer(long.MaxValue, pointer, out _));
        cache.Truncate(4096); // retires a view; the held lease must keep its provenance alive.
        Assert.True(cache.TryDescribePagePointer(0, pointer, out var retired));
        Assert.Equal(original.WindowId, retired.WindowId);
        Assert.True(retired.Retired);
        cache.ReleasePageLease(token);
        Assert.False(cache.TryDescribePagePointer(0, pointer, out _));
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void FilePageIdentity_LogicalIdentityIsStableAcrossAliasesAndDistinctFromRawPointer(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096);
        env.Map();
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        Assert.True(env.Inode.TryGetMappingPageRecord(0, out var record));
        Assert.True(env.Memory.PageMapping.TryGetBinding(MappedFileBackendTestEnvironment.Address, out var binding));

        var inodeIdentity = env.Inode.GetFilePageIdentity(0);
        Assert.True(inodeIdentity.IsValid);
        Assert.Equal(env.Inode.Ino, inodeIdentity.InodeNumber);
        Assert.Equal(0u, inodeIdentity.PageIndex);

        Assert.Equal(inodeIdentity, record.FileIdentity);
        Assert.Equal(inodeIdentity, binding!.Value.FileIdentity);
        Assert.Equal(inodeIdentity, binding.Value.Page!.Value.FileIdentity);
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void T06_ProtectionChange_ProtNoneRaisesFaultAndRestorationPreservesDirectLease(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096);
        env.Map();
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        var before = env.TotalPooledAllocations;

        // Reprotect to PROT_NONE
        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(env.Memory, env.Engine, MappedFileBackendTestEnvironment.Address, 4096, Protection.None));
        Assert.Equal(FaultResult.Segv, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));

        // Restore to Read | Exec
        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(env.Memory, env.Engine, MappedFileBackendTestEnvironment.Address, 4096, Protection.Read | Protection.Exec));
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(MappedFileBackendTestEnvironment.Address));
        Assert.Equal(before, env.TotalPooledAllocations);
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void T11_IdentityAndOrphanLifetime_CloseAndRecreateWithSameNameRetainsOriginalView(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096);
        env.Map();
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
        var originalIdentity = env.Inode.GetFilePageIdentity(0);

        // Close descriptor while mapped; mapping stays functional.
        env.CloseFile();
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(MappedFileBackendTestEnvironment.Address));

        // Create a brand new file with a different name.
        var location = env.Syscalls.PathWalk("/data.bin");
        Assert.True(location.IsValid);
        var dir = env.Syscalls.Root.Dentry!;
        var newFileDentry = new Dentry(FsName.FromString("data_new.bin"), null, dir, dir.SuperBlock);
        Assert.Equal(0, dir.Inode!.Create(newFileDentry, 0x1A4, 0, 0));
        var newFile = new LinuxFile(newFileDentry, FileFlags.O_RDWR, location.Mount);
        dir.Inode.Open(newFile);
        var newInode = (MappingBackedInode)newFileDentry.Inode!;
        newInode.Truncate(4096);
        var newIdentity = newInode.GetFilePageIdentity(0);
        Assert.NotEqual(originalIdentity.InodeNumber, newIdentity.InodeNumber);

        // Map the new file at an adjacent address.
        const uint secondAddr = MappedFileBackendTestEnvironment.Address + 8192;
        newFile.Get();
        try
        {
            env.Memory.Mmap(secondAddr, 4096, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, newFile, 0, "second", env.Engine);
        }
        catch
        {
            newFile.Close();
            throw;
        }
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(secondAddr, false, env.Engine));

        Assert.True(env.Memory.PageMapping.TryGetBinding(MappedFileBackendTestEnvironment.Address, out var originalBinding));
        Assert.True(env.Memory.PageMapping.TryGetBinding(secondAddr, out var newBinding));
        Assert.Equal(originalIdentity, originalBinding!.Value.FileIdentity);
        Assert.Equal(newIdentity, newBinding!.Value.FileIdentity);

        newFile.Close();
        env.Memory.Munmap(secondAddr, 4096, env.Engine);
        env.Memory.Munmap(MappedFileBackendTestEnvironment.Address, 4096, env.Engine);
    }

    [Fact]
    public void T18_InjectedFailure_BackendProviderHandlesErrorsCleanly()
    {
        var fake = new FakeHostMemoryBackend();
        try
        {
            FilePageBackendSelector.FactoryOverrideForTests = (_, _) => fake;
            using var env = new MappedFileBackendTestEnvironment("hostfs", 4096);
            env.Map();
            fake.FailLeaseAcquisition = true;

            // When lease acquisition fails, the mapping falls back to allocated buffer cache,
            // never misreporting that it is using a direct mapped view.
            Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));
            Assert.True(env.Inode.TryGetMappingPageRecord(0, out var record));
            Assert.Equal(FilePageBackingKind.AllocatedPageCache, record.BackingKind);
            Assert.False(((IHostMappedCacheDropper)env.Inode).TryDescribeMappedPage(0, record.Ptr, out _));

            // Test capability error injections
            fake.FailLock = true;
            Assert.False(fake.TryLockPageRange(0, 1, out var lockedPages));
            Assert.Equal(0, lockedPages);

            fake.FailPrefault = true;
            Assert.False(fake.TryPrefaultPage(0, false));

            fake.ConfiguredResidency = HostPageResidencyStatus.NonResident;
            Assert.Equal(HostPageResidencyStatus.NonResident, fake.CheckPageResidency(0));
        }
        finally
        {
            FilePageBackendSelector.FactoryOverrideForTests = null;
            fake.Dispose();
        }
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void HostCapability_ResidencyAndLockLifecycle_ReportsAccurately(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 8192);
        env.Map();
        Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(MappedFileBackendTestEnvironment.Address, false, env.Engine));

        var dropper = (IHostMappedCacheDropper)env.Inode;
        Assert.True(dropper.TryPrefaultPage(0, false));
        var residency = dropper.CheckPageResidency(0);
        Assert.True(residency is HostPageResidencyStatus.Resident or HostPageResidencyStatus.Unknown);

        if (dropper.TryLockPageRange(0, 1, out var lockedPages))
        {
            Assert.Equal(1, lockedPages);
            Assert.True(dropper.TryUnlockPageRange(0, 1));
        }
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void T09_HolePunch_ZeroesRangeAndFreesWholePages(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 3 * 4096);
        env.Map(length: 3 * 4096);
        const uint addr = MappedFileBackendTestEnvironment.Address;

        // Verify initial patterns
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr));
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(1), env.Read(addr + 4096));
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(2), env.Read(addr + 8192));

        // Punch hole in middle page (4096..8192)
        Assert.Equal(0, env.Inode.PunchHole(4096, 4096, default));

        // File size must be preserved
        Assert.Equal(3 * 4096u, env.Inode.Size);

        // Page 0 unchanged, page 1 reads zeroes, page 2 unchanged
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr));
        Assert.Equal((byte)0, env.Read(addr + 4096));
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(2), env.Read(addr + 8192));
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void T09_PartialHolePunch_OnReadOnlyMappedView_ZeroesWithoutHostCrash(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 4096);
        env.File.Get();
        env.Memory.Mmap(MappedFileBackendTestEnvironment.Address, 4096, Protection.Read,
            MapFlags.Shared | MapFlags.Fixed, env.File, 0, "ro-punch", env.Engine);
        const uint addr = MappedFileBackendTestEnvironment.Address;

        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr));
        Assert.Equal(0, env.Inode.PunchHole(1, 16, default));

        Assert.Equal(4096u, env.Inode.Size);
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr));
        Assert.Equal((byte)0, env.Read(addr + 1));
        Assert.Equal((byte)0, env.Read(addr + 16));
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr + 17));
    }

    [Theory]
    [InlineData("hostfs")]
    [InlineData("silkfs")]
    public void T09_HolePunch_InvalidatesCleanPrivateMapping_WhilePreservingCowPages(string backend)
    {
        using var env = new MappedFileBackendTestEnvironment(backend, 2 * 4096);
        env.Map(shared: false, length: 2 * 4096);
        const uint addr = MappedFileBackendTestEnvironment.Address;

        // Read page 0 (clean private mapping) and COW-write page 1.
        Assert.Equal(MappedFileBackendTestEnvironment.Pattern(0), env.Read(addr));
        Assert.True(env.Engine.CopyToUser(addr + 4096, new byte[] { 0xE7 }));

        Assert.Equal(0, env.Inode.PunchHole(0, 2 * 4096, default));

        Assert.Equal((byte)0, env.Read(addr));
        Assert.Equal((byte)0xE7, env.Read(addr + 4096));
    }

    [Fact]
    public void T13_HostLockFailure_PreventsMarkingVmaLocked_AndUnlockBalancesSuccess()
    {
        var fake = new FakeHostMemoryBackend();
        try
        {
            FilePageBackendSelector.FactoryOverrideForTests = (_, _) => fake;
            using var env = new MappedFileBackendTestEnvironment("hostfs", 4096);
            env.Map();
            const uint addr = MappedFileBackendTestEnvironment.Address;

            fake.FailLock = true;
            Assert.Equal(-(int)Errno.ENOMEM, env.Memory.LockRange(addr, 4096, false, env.Engine));
            Assert.Equal((MapFlags)0, env.Memory.FindVmArea(addr)!.Flags & MapFlags.Locked);

            fake.FailLock = false;
            Assert.Equal(0, env.Memory.LockRange(addr, 4096, false, env.Engine));
            Assert.NotEqual((MapFlags)0, env.Memory.FindVmArea(addr)!.Flags & MapFlags.Locked);
            Assert.True(fake.TotalLockedPages >= 1);

            Assert.Equal(0, env.Memory.UnlockRange(addr, 4096));
            Assert.Equal(0, fake.TotalLockedPages);
        }
        finally
        {
            FilePageBackendSelector.FactoryOverrideForTests = null;
            fake.Dispose();
        }
    }

    [Fact]
    public void T13_LockedFilePage_CannotBeEvictedUnderProtNone_AndTruncateReleasesAndReacquiresLock()
    {
        var fake = new FakeHostMemoryBackend();
        try
        {
            FilePageBackendSelector.FactoryOverrideForTests = (_, _) => fake;
            using var env = new MappedFileBackendTestEnvironment("hostfs", 4096);
            env.Map();
            const uint addr = MappedFileBackendTestEnvironment.Address;

            Assert.Equal(0, env.Memory.LockRange(addr, 4096, false, env.Engine));
            Assert.Equal(1, fake.TotalLockedPages);

            Assert.Equal(0, env.Memory.Mprotect(addr, 4096, Protection.None, env.Engine, out _));
            var vma = env.Memory.FindVmArea(addr)!;
            Assert.False(vma.VmMapping!.TryEvictCleanPage(vma.GetPageIndex(addr)));

            Assert.Equal(0, env.Inode.Truncate(0));
            Assert.Equal(0, fake.TotalLockedPages);

            Assert.Equal(0, env.Inode.Truncate(4096));
            Assert.Equal(0, env.Memory.Mprotect(addr, 4096, Protection.Read, env.Engine, out _));
            Assert.Equal(FaultResult.Handled, env.Memory.HandleFaultDetailed(addr, false, env.Engine));
            Assert.Equal(1, fake.TotalLockedPages);

            Assert.Equal(0, env.Memory.UnlockRange(addr, 4096));
            Assert.Equal(0, fake.TotalLockedPages);
        }
        finally
        {
            FilePageBackendSelector.FactoryOverrideForTests = null;
            fake.Dispose();
        }
    }

    [Fact]
    public void T13_OnFaultLockFailure_PropagatesOom_AndLeavesNoDanglingPteOrLock()
    {
        var fake = new FakeHostMemoryBackend();
        try
        {
            FilePageBackendSelector.FactoryOverrideForTests = (_, _) => fake;
            using var env = new MappedFileBackendTestEnvironment("hostfs", 4096);
            env.Map();
            const uint addr = MappedFileBackendTestEnvironment.Address;

            Assert.Equal(0, env.Memory.LockRange(addr, 4096, true, env.Engine));
            Assert.Equal(0, fake.TotalLockedPages);

            fake.FailLock = true;
            Assert.Equal(FaultResult.Oom, env.Memory.HandleFaultDetailed(addr, false, env.Engine));
            Assert.False(env.Memory.PageMapping.TryGet(addr, out _));
            Assert.Equal(0, fake.TotalLockedPages);
        }
        finally
        {
            FilePageBackendSelector.FactoryOverrideForTests = null;
            fake.Dispose();
        }
    }
}
