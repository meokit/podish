using Xunit;

namespace Fiberish.SilkFS.Tests;

public class SilkRepositoryTests
{
    [Fact]
    public void Initialize_CreatesMetadataAndObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);

            repo.Initialize();

            Assert.True(Directory.Exists(options.RootPath));
            Assert.True(Directory.Exists(options.ObjectsPath));
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
    public void MetadataStore_InodeObject_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var inode = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);
            var payload = "silk-content"u8.ToArray();
            var objectId = repo.PutObject(payload);
            repo.Metadata.SetInodeObject(inode, objectId);

            var lookedUpObject = repo.Metadata.GetInodeObject(inode);
            Assert.Equal(objectId, lookedUpObject);

            var loaded = repo.ReadObject(lookedUpObject!);
            Assert.NotNull(loaded);
            Assert.Equal(payload, loaded);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_ObjectRefCount_TracksReplaceAndDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            var ino1 = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);
            var ino2 = repo.Metadata.CreateInode(SilkInodeKind.File, 0x1A4);

            var objA = repo.PutObject("object-a"u8.ToArray());
            var objB = repo.PutObject("object-b"u8.ToArray());

            var b1 = repo.Metadata.SetInodeObjectWithRefCount(ino1, objA);
            Assert.True(b1.Changed);
            Assert.Null(b1.UnreferencedObjectId);
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(objA));

            var b2 = repo.Metadata.SetInodeObjectWithRefCount(ino2, objA);
            Assert.True(b2.Changed);
            Assert.Null(b2.UnreferencedObjectId);
            Assert.Equal(2, repo.Metadata.GetObjectRefCount(objA));

            var b3 = repo.Metadata.SetInodeObjectWithRefCount(ino1, objA);
            Assert.False(b3.Changed);
            Assert.Equal(2, repo.Metadata.GetObjectRefCount(objA));

            var b4 = repo.Metadata.SetInodeObjectWithRefCount(ino1, objB);
            Assert.True(b4.Changed);
            Assert.Null(b4.UnreferencedObjectId);
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(objA));
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(objB));

            var deletedA = repo.Metadata.DeleteInodeWithObjectRefCount(ino2);
            Assert.Equal(objA, deletedA);
            Assert.Equal(0, repo.Metadata.GetObjectRefCount(objA));

            var deletedB = repo.Metadata.DeleteInodeWithObjectRefCount(ino1);
            Assert.Equal(objB, deletedB);
            Assert.Equal(0, repo.Metadata.GetObjectRefCount(objB));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
