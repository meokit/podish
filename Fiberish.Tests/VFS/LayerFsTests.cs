using System.Text;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class LayerFsTests
{
    private sealed class OffsetContentProvider(byte[] blob) : ILayerContentProvider
    {
        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            if (entry.Type != InodeType.File) return true;
            if (entry.DataOffset < 0) return false;

            var start = (int)(entry.DataOffset + offset);
            if (start >= blob.Length) return true;

            var maxByBlob = blob.Length - start;
            var maxBySize = (int)Math.Max(0, (long)entry.Size - offset);
            var toCopy = Math.Min(buffer.Length, Math.Min(maxByBlob, maxBySize));
            if (toCopy <= 0) return true;

            blob.AsSpan(start, toCopy).CopyTo(buffer);
            bytesRead = toCopy;
            return true;
        }
    }

    [Fact]
    public void Lookup_IsCaseSensitive()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("Readme", "hello"u8.ToArray()));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var hit = sb.Root.Inode!.Lookup("Readme");
        var miss = sb.Root.Inode!.Lookup("readme");

        Assert.NotNull(hit);
        Assert.Null(miss);
    }

    [Fact]
    public void Read_FileContent_Works()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_RDONLY, null!);
        var buf = new byte[8];
        var n = d!.Inode!.Read(lf, buf, 0);

        Assert.Equal(3, n);
        Assert.Equal("abc", Encoding.UTF8.GetString(buf, 0, n));
    }

    [Fact]
    public void Readlink_Symlink_Works()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.Symlink("sh", "/bin/busybox"));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("sh");
        Assert.NotNull(d);
        Assert.Equal("/bin/busybox", d!.Inode!.Readlink());
    }

    [Fact]
    public void Write_ReturnsErofs()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_WRONLY, null!);
        var rc = d!.Inode!.Write(lf, "z"u8.ToArray(), 0);

        Assert.Equal(-(int)Errno.EROFS, rc);
    }

    [Fact]
    public void MountFromIndex_PreservesMetadata()
    {
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry(
            "/bin",
            InodeType.Directory,
            Mode: 0x1ED,
            Uid: 1000,
            Gid: 1001));
        index.AddEntry(new LayerIndexEntry(
            "/bin/app",
            InodeType.File,
            Mode: 0x1ED,
            Uid: 2000,
            Gid: 2001,
            Size: 3,
            InlineData: "abc"u8.ToArray()));

        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Index = index });

        var bin = sb.Root.Inode!.Lookup("bin");
        Assert.NotNull(bin);
        Assert.Equal(0x1ED, bin!.Inode!.Mode);
        Assert.Equal(1000, bin.Inode.Uid);
        Assert.Equal(1001, bin.Inode.Gid);

        var app = bin.Inode.Lookup("app");
        Assert.NotNull(app);
        Assert.Equal(0x1ED, app!.Inode!.Mode);
        Assert.Equal(2000, app.Inode.Uid);
        Assert.Equal(2001, app.Inode.Gid);
    }

    [Fact]
    public void Read_FromIndexOffset_UsesContentProvider()
    {
        var blob = Encoding.UTF8.GetBytes("xxpayloadyy");
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry(
            "/f",
            InodeType.File,
            Mode: 0x1A4,
            Size: 7,
            DataOffset: 2));

        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions
            {
                Index = index,
                ContentProvider = new OffsetContentProvider(blob)
            });

        var d = sb.Root.Inode!.Lookup("f");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_RDONLY, null!);
        var buf = new byte[16];
        var n = d!.Inode!.Read(lf, buf, 0);

        Assert.Equal(7, n);
        Assert.Equal("payload", Encoding.UTF8.GetString(buf, 0, n));
    }
}
