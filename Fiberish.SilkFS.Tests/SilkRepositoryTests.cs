using Xunit;

namespace Fiberish.SilkFS.Tests;

public class SilkRepositoryTests
{
    [Fact]
    public void Initialize_CreatesMetadataAndLiveStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);

            repo.Initialize();

            Assert.True(Directory.Exists(options.RootPath));
            Assert.True(Directory.Exists(options.LiveDataPath));
            Assert.True(File.Exists(options.MetadataPath));
            Assert.True(repo.Metadata.InodeExists(SilkMetadataStore.RootInode));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_CanCreateDentryAndXAttr()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var inode = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4); // 0644
            repo.Metadata.UpsertDentry(SilkMetadataStore.RootInode, "hello.txt", inode);
            var lookup = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "hello.txt");

            Assert.Equal(inode, lookup);

            repo.Metadata.SetXAttr(inode, "user.mime_type", "text/plain"u8.ToArray());
            var v = repo.Metadata.GetXAttr(inode, "user.mime_type");
            Assert.NotNull(v);
            Assert.Equal("text/plain", System.Text.Encoding.UTF8.GetString(v!));

            var list = repo.Metadata.ListXAttrs(inode);
            Assert.True(list.ContainsKey("user.mime_type"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_WhiteoutAndOpaque_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            Assert.False(repo.Metadata.HasWhiteout(SilkMetadataStore.RootInode, "ghost.txt"));
            repo.Metadata.MarkWhiteout(SilkMetadataStore.RootInode, "ghost.txt");
            Assert.True(repo.Metadata.HasWhiteout(SilkMetadataStore.RootInode, "ghost.txt"));
            repo.Metadata.ClearWhiteout(SilkMetadataStore.RootInode, "ghost.txt");
            Assert.False(repo.Metadata.HasWhiteout(SilkMetadataStore.RootInode, "ghost.txt"));

            Assert.False(repo.Metadata.IsOpaque(SilkMetadataStore.RootInode));
            repo.Metadata.MarkOpaque(SilkMetadataStore.RootInode);
            Assert.True(repo.Metadata.IsOpaque(SilkMetadataStore.RootInode));
            repo.Metadata.ClearOpaque(SilkMetadataStore.RootInode);
            Assert.False(repo.Metadata.IsOpaque(SilkMetadataStore.RootInode));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LiveStore_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var inode = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);
            var payload = "silk-content"u8.ToArray();
            repo.WriteLiveInodeData(inode, payload);

            var loaded = repo.ReadLiveInodeData(inode);
            Assert.NotNull(loaded);
            Assert.Equal(payload, loaded);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LiveStore_TruncateAndDelete_Work()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var ino = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);
            repo.WriteLiveInodeData(ino, "object-a"u8.ToArray());
            Assert.Equal("object-a", System.Text.Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));

            repo.TruncateLiveInodeData(ino, 3);
            Assert.Equal("obj", System.Text.Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));

            repo.DeleteLiveInodeData(ino);
            Assert.Null(repo.ReadLiveInodeData(ino));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WriteLiveInodeData_RewritesInPlace_AndResizesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var ino = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);
            repo.WriteLiveInodeData(ino, "abcdef"u8.ToArray());
            repo.WriteLiveInodeData(ino, "xy"u8.ToArray());

            var path = repo.GetLiveInodePath(ino);
            Assert.True(File.Exists(path));
            Assert.Equal(2, new FileInfo(path).Length);
            Assert.Equal("xy", System.Text.Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
