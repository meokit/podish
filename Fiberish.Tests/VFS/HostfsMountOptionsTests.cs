using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsMountOptionsTests
{
    [Fact]
    public void Parse_Empty()
    {
        var opts = HostfsMountOptions.Parse(null);
        Assert.Null(opts.MountUid);
        Assert.Null(opts.MountGid);
        Assert.Equal(-1, opts.Umask);
        Assert.Equal(-1, opts.Fmask);
        Assert.Equal(-1, opts.Dmask);
        Assert.True(opts.MetadataLess);
        Assert.Equal(HostfsMountBoundaryMode.SingleDomain, opts.MountBoundaryMode);
        Assert.Equal(HostfsSpecialNodeMode.Strict, opts.SpecialNodeMode);
    }

    [Fact]
    public void Parse_Full()
    {
        var opts = HostfsMountOptions.Parse("uid=1000,gid=1000,umask=022,fmask=011,dmask=000,metadata=1");
        Assert.Equal(1000, opts.MountUid);
        Assert.Equal(1000, opts.MountGid);
        Assert.Equal(18, opts.Umask); // 022 octal = 18 decimal
        Assert.Equal(9, opts.Fmask); // 011 octal = 9 decimal
        Assert.Equal(0, opts.Dmask);
        Assert.False(opts.MetadataLess);
    }

    [Fact]
    public void Parse_MetadataDisabledExplicitly()
    {
        var opts = HostfsMountOptions.Parse("metadata=0");
        Assert.True(opts.MetadataLess);
    }

    [Fact]
    public void ApplyModeMask_UmaskOnly()
    {
        var opts = HostfsMountOptions.Parse("umask=022");
        // Mode 0777 -> 0755 (022 removed)
        Assert.Equal(0x1ED, opts.ApplyModeMask(true, 0x1FF));
        // Mode 0666 -> 0644 (022 removed)
        Assert.Equal(0x1A4, opts.ApplyModeMask(false, 0x1B6));
    }

    [Fact]
    public void ApplyModeMask_FmaskDmask()
    {
        var opts = HostfsMountOptions.Parse("fmask=0111,dmask=000");

        // Directory: 0777 -> 0777 (dmask 000)
        Assert.Equal(0x1FF, opts.ApplyModeMask(true, 0x1FF));

        // File: 0777 -> 0666 (fmask 111 removes exec bits)
        Assert.Equal(0x1B6, opts.ApplyModeMask(false, 0x1FF));
    }

    [Fact]
    public void Parse_PolicyModes()
    {
        var opts = HostfsMountOptions.Parse("mount_boundary=passthrough,special_node=passthrough");
        Assert.Equal(HostfsMountBoundaryMode.Passthrough, opts.MountBoundaryMode);
        Assert.Equal(HostfsSpecialNodeMode.Passthrough, opts.SpecialNodeMode);
    }
}