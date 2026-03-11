using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class EngineMmuTransferTests
{
    [Fact]
    public void DetachThenAttach_RestoresMappings_AndConsumesHandle()
    {
        using var engine = new Engine();
        const uint addr = 0x00400000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        Marshal.WriteByte(page, 0x5A);

        var beforeIdentity = engine.CurrentMmuIdentity;
        var detached = engine.DetachMmu();
        Assert.NotEqual(beforeIdentity, engine.CurrentMmuIdentity);

        var read = new byte[1];
        Assert.False(engine.CopyFromUser(addr, read));

        engine.ReplaceMmu(detached);
        Assert.Equal(beforeIdentity, engine.CurrentMmuIdentity);

        Assert.True(engine.CopyFromUser(addr, read));
        Assert.Equal((byte)0x5A, read[0]);
        detached.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.ReplaceMmu(detached));
    }

    [Fact]
    public void CloneMmu_DropsExternalMappings_ButKeepsOwnedPages()
    {
        using var parent = new Engine();
        using var child = new Engine();
        const uint externalAddr = 0x00500000;
        const uint ownedAddr = 0x00600000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x11;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x22);

            using var cloned = parent.CurrentMmu.CloneSkipExternal();
            child.ReplaceMmu(cloned);

            var read = new byte[1];
            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x22, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void CloneFork_DropsExternalMappings_ByDefault()
    {
        using var parent = new Engine();
        const uint externalAddr = 0x00700000;
        const uint ownedAddr = 0x00800000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x44;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x66);

            using var child = parent.Clone(shareMem: false);
            Assert.NotEqual(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);

            var read = new byte[1];
            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x66, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void CloneForkWithSkipMmu_AllowsManualCloneAttachFlow()
    {
        using var parent = new Engine();
        const uint externalAddr = 0x00900000;
        const uint ownedAddr = 0x00A00000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x7A;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x3C);

            using var child = parent.Clone(shareMem: false, memoryMode: EngineCloneMemoryMode.SkipMmu);
            var read = new byte[1];
            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.False(child.CopyFromUser(ownedAddr, read));

            using var cloned = parent.CurrentMmu.CloneSkipExternal();
            child.ReplaceMmu(cloned);

            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x3C, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void ShareMmuFrom_UsesSameMmuIdentity()
    {
        using var parent = new Engine();
        var sharedId = parent.CurrentMmuIdentity;
        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));

        using (var child = new Engine())
        {
            child.ShareMmuFrom(parent);
            Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);
            Assert.Equal(2, Engine.GetAttachmentCount(sharedId));
        }

        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));
    }
}
