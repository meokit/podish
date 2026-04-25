using Fiberish.VFS;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class OciLayerIndexMergerTests
{
    [Fact]
    public void Merge_Whiteout_RemovesTargetPathAndDescendants()
    {
        var merged = OciLayerIndexMerger.Merge(
        [
            [
                new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED),
                new LayerIndexEntry("/etc/apk", InodeType.Directory, 0x1ED),
                new LayerIndexEntry("/etc/apk/repositories", InodeType.File, 0x1A4, Size: 4),
                new LayerIndexEntry("/etc/hostname", InodeType.File, 0x1A4, Size: 4)
            ],
            [
                new LayerIndexEntry("/etc/.wh.apk", InodeType.File, 0x1A4)
            ]
        ]);

        Assert.True(merged.TryGetEntry("/etc", out var etc));
        Assert.Equal(InodeType.Directory, etc.Type);
        Assert.False(merged.TryGetEntry("/etc/apk", out _));
        Assert.False(merged.TryGetEntry("/etc/apk/repositories", out _));
        Assert.True(merged.TryGetEntry("/etc/hostname", out _));
    }

    [Fact]
    public void Merge_OpaqueWhiteout_RemovesExistingChildrenBeforeApplyingLayer()
    {
        var merged = OciLayerIndexMerger.Merge(
        [
            [
                new LayerIndexEntry("/usr", InodeType.Directory, 0x1ED),
                new LayerIndexEntry("/usr/bin", InodeType.Directory, 0x1ED),
                new LayerIndexEntry("/usr/bin/old-app", InodeType.File, 0x1ED, Size: 3)
            ],
            [
                new LayerIndexEntry("/usr/bin/.wh..wh..opq", InodeType.File, 0x1A4),
                new LayerIndexEntry("/usr/bin/new-app", InodeType.File, 0x1ED, Size: 7)
            ]
        ]);

        Assert.True(merged.TryGetEntry("/usr/bin", out var bin));
        Assert.Equal(InodeType.Directory, bin.Type);
        Assert.False(merged.TryGetEntry("/usr/bin/old-app", out _));
        Assert.True(merged.TryGetEntry("/usr/bin/new-app", out _));
    }

    [Fact]
    public void Merge_CreatesMissingParentDirectories()
    {
        var merged = OciLayerIndexMerger.Merge(
        [
            [
                new LayerIndexEntry("/var/log/podish/boot.log", InodeType.File, 0x1A4, Size: 8)
            ]
        ]);

        Assert.True(merged.TryGetEntry("/", out var root));
        Assert.Equal(InodeType.Directory, root.Type);
        Assert.True(merged.TryGetEntry("/var", out var varDir));
        Assert.Equal(InodeType.Directory, varDir.Type);
        Assert.True(merged.TryGetEntry("/var/log", out var logDir));
        Assert.Equal(InodeType.Directory, logDir.Type);
        Assert.True(merged.TryGetEntry("/var/log/podish", out var podishDir));
        Assert.Equal(InodeType.Directory, podishDir.Type);
        Assert.True(merged.TryGetEntry("/var/log/podish/boot.log", out var bootLog));
        Assert.Equal(InodeType.File, bootLog.Type);
    }
}
