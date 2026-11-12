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
}
