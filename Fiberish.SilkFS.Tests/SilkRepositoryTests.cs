using System.Text;

namespace Fiberish.SilkFS.Tests;

public class SilkRepositoryTests
{
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

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
            using (var session = repo.OpenMetadataSession())
                Assert.True(session.InodeExists(SilkMetadataStore.RootInode));
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

            using var session = repo.OpenMetadataSession();
            var inode = session.CreateInode(SilkInodeKind.File, 0x1A4); // 0644
            session.UpsertDentry(SilkMetadataStore.RootInode, Utf8("hello.txt"), inode);
            var lookup = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("hello.txt"));

            Assert.Equal(inode, lookup);

            session.SetXAttr(inode, Utf8("user.mime_type"), "text/plain"u8.ToArray());
            var v = session.GetXAttr(inode, Utf8("user.mime_type"));
            Assert.NotNull(v);
            Assert.Equal("text/plain", Encoding.UTF8.GetString(v!));

            var list = session.ListXAttrs(inode);
            Assert.Contains(list, x => x.Key.SequenceEqual(Utf8("user.mime_type")));
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

            using var session = repo.OpenMetadataSession();
            Assert.False(session.HasWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt")));
            session.MarkWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt"));
            Assert.True(session.HasWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt")));
            session.ClearWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt"));
            Assert.False(session.HasWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt")));

            Assert.False(session.IsOpaque(SilkMetadataStore.RootInode));
            session.MarkOpaque(SilkMetadataStore.RootInode);
            Assert.True(session.IsOpaque(SilkMetadataStore.RootInode));
            session.ClearOpaque(SilkMetadataStore.RootInode);
            Assert.False(session.IsOpaque(SilkMetadataStore.RootInode));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_WriteCommands_CanBeReusedAcrossTransactions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            using var session = repo.OpenMetadataSession();
            var inode = session.CreateInode(SilkInodeKind.File, 0x1A4);
            for (var i = 0; i < 8; i++)
            {
                session.UpsertInode(inode, SilkInodeKind.File, 0x1A4, i, i + 1, 1, 0, i * 10L);
                session.UpsertDentry(SilkMetadataStore.RootInode, Utf8($"file-{i}"), inode);
                session.MarkWhiteout(SilkMetadataStore.RootInode, Utf8($"ghost-{i}"));
                session.ClearWhiteout(SilkMetadataStore.RootInode, Utf8($"ghost-{i}"));
            }

            var record = session.GetInode(inode);
            Assert.NotNull(record);
            Assert.Equal(7, record.Value.Uid);
            Assert.Equal(8, record.Value.Gid);
            Assert.Equal(70, record.Value.Size);

            for (var i = 0; i < 8; i++)
                Assert.Equal(inode, session.LookupDentry(SilkMetadataStore.RootInode, Utf8($"file-{i}")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_Utf8NamesAndXAttrs_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            var repo = new SilkRepository(options);
            repo.Initialize();

            using var session = repo.OpenMetadataSession();
            var inode = session.CreateInode(SilkInodeKind.File, 0x1A4);
            session.UpsertDentry(SilkMetadataStore.RootInode, Utf8("你好-😀.txt"), inode);
            session.SetXAttr(inode, Utf8("user.标签"), "值-😀"u8.ToArray());

            Assert.Equal(inode, session.LookupDentry(SilkMetadataStore.RootInode, Utf8("你好-😀.txt")));
            Assert.Contains(session.ListDentriesByParent(SilkMetadataStore.RootInode),
                x => x.Name.SequenceEqual(Utf8("你好-😀.txt")));

            var value = session.GetXAttr(inode, Utf8("user.标签"));
            Assert.NotNull(value);
            Assert.Equal("值-😀", Encoding.UTF8.GetString(value!));
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

            using var session = repo.OpenMetadataSession();
            var inode = session.CreateInode(SilkInodeKind.File, 0x1A4);
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

            using var session = repo.OpenMetadataSession();
            var ino = session.CreateInode(SilkInodeKind.File, 0x1A4);
            repo.WriteLiveInodeData(ino, "object-a"u8.ToArray());
            Assert.Equal("object-a", Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));

            repo.TruncateLiveInodeData(ino, 3);
            Assert.Equal("obj", Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));

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

            using var session = repo.OpenMetadataSession();
            var ino = session.CreateInode(SilkInodeKind.File, 0x1A4);
            repo.WriteLiveInodeData(ino, "abcdef"u8.ToArray());
            repo.WriteLiveInodeData(ino, "xy"u8.ToArray());

            var path = repo.GetLiveInodePath(ino);
            Assert.True(File.Exists(path));
            Assert.Equal(2, new FileInfo(path).Length);
            Assert.Equal("xy", Encoding.UTF8.GetString(repo.ReadLiveInodeData(ino)!));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MetadataStore_InvalidUtf8NamesAndKeys_RoundTripAsRawBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var repo = new SilkRepository(SilkFsOptions.FromSource(root));
            repo.Initialize();

            using var session = repo.OpenMetadataSession();
            var inode = session.CreateInode(SilkInodeKind.File, 0x1A4);
            var badName = new byte[] { 0xE4, 0xB8, 0xAD, 0xFF, 0x2E, 0x74, 0x78, 0x74 };
            var badKey = new byte[] { 0x75, 0x73, 0x65, 0x72, 0x2E, 0xFF };

            session.UpsertDentry(SilkMetadataStore.RootInode, badName, inode);
            session.SetXAttr(inode, badKey, new byte[] { 1, 2, 3 });

            Assert.Equal(inode, session.LookupDentry(SilkMetadataStore.RootInode, badName));
            Assert.Contains(session.ListDentriesByParent(SilkMetadataStore.RootInode), x => x.Name.SequenceEqual(badName));
            Assert.Equal(new byte[] { 1, 2, 3 }, session.GetXAttr(inode, badKey));
            Assert.Contains(session.ListXAttrs(inode), x => x.Key.SequenceEqual(badKey));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Initialize_FailsFastOnOldSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-{Guid.NewGuid():N}");
        try
        {
            var options = SilkFsOptions.FromSource(root);
            Directory.CreateDirectory(options.RootPath);
            Directory.CreateDirectory(options.LiveDataPath);
            File.WriteAllBytes(options.MetadataPath, Array.Empty<byte>());

            using (var conn = new SilkSqliteConnection(options.MetadataPath))
            {
                conn.ExecuteNonQuery("""
                                     CREATE TABLE meta (
                                       k TEXT PRIMARY KEY,
                                       v TEXT NOT NULL
                                     )
                                     """u8);
                using var stmt = conn.Prepare("INSERT INTO meta(k, v) VALUES ('schema_version', '2')"u8, false);
                stmt.ExecuteNonQuery();
            }

            var repo = new SilkRepository(options);
            var ex = Assert.Throws<InvalidOperationException>(() => repo.Initialize());
            Assert.Contains("schema v2", ex.Message);
            Assert.Contains("expected v3", ex.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
