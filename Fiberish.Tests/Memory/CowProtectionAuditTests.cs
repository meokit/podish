using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

public sealed class CowProtectionAuditTests
{
    [Fact]
    public void CowCopy_QuotaPressureCannotReclaimItsOwnSourcePage()
    {
        using var context = new MemoryRuntimeContext();
        using var engine = new Engine(context);
        var mm = new VMAManager(context);
        var cache = new AddressSpace(context, AddressSpaceKind.File);
        context.AddressSpacePolicy.TrackAddressSpace(cache);
        var source = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
        Assert.NotEqual(IntPtr.Zero, source);
        // Even after evicting the source, quota is too small for the copy. The
        // unfixed path therefore cannot dereference freed memory in this test:
        // it returns OOM, but wrongly destroys the source cache entry first.
        context.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        var method = typeof(VMAManager).GetMethod("TryAllocatePrivateCopyFromSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object?[] args = [engine, 0x47000000u, cache.PeekVmPage(0)!.Value.HostPage,
            "AuditCowSource", null, AllocationSource.CowFirstPrivate];
        BackingPageHandle copy = default;
        try
        {
            bool copied = (bool)method.Invoke(mm, args)!;
            copy = (BackingPageHandle)args[4]!;
            Assert.False(copied);
            Assert.Equal(1, cache.PageCount);
            Assert.Equal(0, context.HostPages.GetRequired(source).PinCount); // failure releases the pin
        }
        finally
        {
            BackingPageHandle.Release(ref copy);
            cache.Release();
            mm.Clear(engine);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MprotectWrite_DoesNotMakeZeroOrForkSharedBackingWritable(bool forkPrivatePage)
    {
        using var context = new MemoryRuntimeContext();
        var runtime = new TestRuntimeFactory(context);
        using var parent = runtime.CreateEngine();
        var parentMm = runtime.CreateAddressSpace();
        parent.PageFaultResolver = (addr, write) => parentMm.HandleFault(addr, write, parent);
        const uint address = 0x47000000;
        parentMm.Mmap(address, 8192, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed, null, 0, "cow-audit", parent);
        Assert.True(parent.CopyFromUser(address, new byte[1]));
        Assert.True(parent.CopyFromUser(address + 4096, new byte[1]));
        if (forkPrivatePage) Assert.True(parent.CopyToUser(address, new byte[] { 42 }));

        var childMm = parentMm.Clone();
        using var child = parent.Clone(false);
        childMm.RebuildPageBindingsFromNative(child, childMm.VMAs);
        child.PageFaultResolver = (addr, write) => childMm.HandleFault(addr, write, child);
        try
        {
            parentMm.ReprotectNativeMappings(parent, address, 8192, Protection.Read, false);
            childMm.ReprotectNativeMappings(child, address, 8192, Protection.Read, false);
            Assert.Equal(0, childMm.Mprotect(address, 8192, Protection.Read, child, out _));
            Assert.Equal(0, childMm.Mprotect(address, 8192, Protection.Read | Protection.Write, child, out _));
            // Check native protection before writing: the red test must not
            // actually corrupt a shared zero backing page used by other tests.
            Assert.Equal(IntPtr.Zero, child.GetPhysicalAddressSafe(address, true));
            Assert.True(child.CopyToUser(address, new byte[] { 99 }));
            var result = new byte[1];
            Assert.True(parent.CopyFromUser(address, result));
            Assert.Equal(forkPrivatePage ? (byte)42 : (byte)0, result[0]);
            Assert.True(child.CopyFromUser(address, result));
            Assert.Equal((byte)99, result[0]);
            Assert.True(child.CopyFromUser(address + 4096, result));
            Assert.Equal((byte)0, result[0]);
        }
        finally
        {
            childMm.Clear(child);
            parentMm.Clear(parent);
        }
    }
}
