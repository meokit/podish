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

    [Fact]
    public void BuildFromTar_HandlesPaxLongPathHardlinksAndDotSlashPrefix()
    {
        var longPath = "usr/share/icons/breeze/apps/48/" + new string('a', 110) + ".svg";
        var payload = "hello-tarfs"u8.ToArray();

        using var ms = new MemoryStream();
        WritePaxEntry(ms, "path", longPath);
        WriteTarEntry(ms, "./" + longPath[..80], (byte)'0', payload);
        WritePaxEntry(ms, "linkpath", "./" + longPath);
        WriteTarEntry(ms, "./usr/bin/linked-copy", (byte)'1', [], linkName: "./" + longPath[..80]);
        ms.Write(new byte[1024]);
        ms.Position = 0;

        var index = OciLayerIndexBuilder.BuildFromTar(ms, "sha256:test");

        Assert.True(index.TryGetEntry("/" + longPath, out var longEntry));
        Assert.Equal(InodeType.File, longEntry.Type);
        Assert.Equal((ulong)payload.Length, longEntry.Size);
        Assert.True(longEntry.DataOffset >= 0);

        Assert.True(index.TryGetEntry("/usr/bin/linked-copy", out var hardlinkEntry));
        Assert.Equal(InodeType.File, hardlinkEntry.Type);
        Assert.Equal(longEntry.DataOffset, hardlinkEntry.DataOffset);
        Assert.Equal(longEntry.Size, hardlinkEntry.Size);
    }

    private static void WritePaxEntry(Stream stream, string key, string value)
    {
        var rec = $"{key}={value}\n";
        var total = rec.Length + 2;
        while (true)
        {
            var line = $"{total} {rec}";
            if (line.Length == total)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                WriteTarEntry(stream, "PaxHeaders/entry", (byte)'x', bytes);
                return;
            }
            total = line.Length;
        }
    }

    private static void WriteTarEntry(Stream stream, string name, byte typeFlag, byte[] data, string linkName = "")
    {
        var header = new byte[512];
        System.Text.Encoding.UTF8.GetBytes(name).AsSpan(0, Math.Min(name.Length, 100)).CopyTo(header.AsSpan(0));
        System.Text.Encoding.ASCII.GetBytes("0000644\0").CopyTo(header.AsSpan(100));
        System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header.AsSpan(108));
        System.Text.Encoding.ASCII.GetBytes("0000000\0").CopyTo(header.AsSpan(116));
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header.AsSpan(124));
        System.Text.Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header.AsSpan(136));
        header[156] = typeFlag;
        if (!string.IsNullOrEmpty(linkName))
            System.Text.Encoding.UTF8.GetBytes(linkName).AsSpan(0, Math.Min(linkName.Length, 100)).CopyTo(header.AsSpan(157));
        System.Text.Encoding.ASCII.GetBytes("ustar\0").CopyTo(header.AsSpan(257));
        System.Text.Encoding.ASCII.GetBytes("00").CopyTo(header.AsSpan(263));
        stream.Write(header);
        if (data.Length > 0)
        {
            stream.Write(data);
            var pad = (512 - data.Length % 512) % 512;
            if (pad > 0) stream.Write(new byte[pad]);
        }
    }
}
