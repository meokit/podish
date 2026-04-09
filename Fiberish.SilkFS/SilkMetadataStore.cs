using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Fiberish.SilkFS;

public enum SilkInodeKind
{
    File = 1,
    Directory = 2,
    Symlink = 3,
    CharDevice = 4,
    BlockDevice = 5,
    Fifo = 6,
    Socket = 7
}

public readonly record struct SilkInodeRecord(
    long Ino,
    SilkInodeKind Kind,
    int Mode,
    int Uid,
    int Gid,
    long Nlink,
    long Rdev,
    long Size,
    long ATimeNs,
    long MTimeNs,
    long CTimeNs);

public readonly record struct SilkDentryRecord(
    long ParentIno,
    string Name,
    long Ino);

public sealed class SilkMetadataStore
{
    public const long RootInode = 1;
    public const string OpaqueMarkerName = ".wh..wh..opq";

    private static int _sqliteInit;
    private readonly string _connectionString;

    public SilkMetadataStore(string dbPath)
    {
        EnsureSqliteProviderInitialized();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public SilkMetadataSession OpenSession()
    {
        return new SilkMetadataSession(this, OpenConnection());
    }

    public void Initialize()
    {
        using var conn = OpenConnection();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();

        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS meta (
                         k TEXT PRIMARY KEY,
                         v TEXT NOT NULL
                       );
                       """);
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS inodes (
                         ino INTEGER PRIMARY KEY AUTOINCREMENT,
                         kind INTEGER NOT NULL,
                         mode INTEGER NOT NULL,
                         uid INTEGER NOT NULL,
                         gid INTEGER NOT NULL,
                         nlink INTEGER NOT NULL,
                         rdev INTEGER NOT NULL,
                         size INTEGER NOT NULL,
                         atime_ns INTEGER NOT NULL,
                         mtime_ns INTEGER NOT NULL,
                         ctime_ns INTEGER NOT NULL
                       );
                       """);
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS dentries (
                         parent_ino INTEGER NOT NULL,
                         name TEXT NOT NULL,
                         ino INTEGER NOT NULL,
                         PRIMARY KEY (parent_ino, name),
                         FOREIGN KEY (ino) REFERENCES inodes(ino) ON DELETE CASCADE
                       );
                       """);
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS xattrs (
                         ino INTEGER NOT NULL,
                         key TEXT NOT NULL,
                         value BLOB NOT NULL,
                         PRIMARY KEY (ino, key),
                         FOREIGN KEY (ino) REFERENCES inodes(ino) ON DELETE CASCADE
                       );
                       """);
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS whiteouts (
                         parent_ino INTEGER NOT NULL,
                         name TEXT NOT NULL,
                         opaque INTEGER NOT NULL DEFAULT 0,
                         PRIMARY KEY (parent_ino, name)
                       );
                       """);
        Exec(conn, tx, "DROP TABLE IF EXISTS inode_objects;");
        Exec(conn, tx, "DROP TABLE IF EXISTS objects;");
        Exec(conn, tx, """
                       INSERT INTO meta(k, v) VALUES ('schema_version', '2')
                       ON CONFLICT(k) DO UPDATE SET v = excluded.v;
                       """);

        var rootExists = ScalarLong(conn, tx, "SELECT COUNT(1) FROM inodes WHERE ino = 1;");
        if (rootExists == 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO inodes(ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns) VALUES (1, @kind, @mode, 0, 0, 2, 0, 0, @now, @now, @now);";
            cmd.Parameters.AddWithValue("@kind", (int)SilkInodeKind.Directory);
            cmd.Parameters.AddWithValue("@mode", 0x1FF);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void EnsureSqliteProviderInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInit, 1) != 0)
            return;
        raw.SetProvider(new SQLite3Provider_sqlite3());
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public sealed class SilkMetadataTransaction : IDisposable
    {
        private readonly SilkMetadataSession _session;
        private readonly SqliteTransaction _tx;
        private bool _disposed;

        internal SilkMetadataTransaction(SilkMetadataSession session, SqliteTransaction tx)
        {
            _session = session;
            _tx = tx;
        }

        public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
            long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var effectiveAtime = atimeNs ?? now;
            var effectiveMtime = mtimeNs ?? now;
            var effectiveCtime = ctimeNs ?? now;
            var cmd = _session.GetUpsertInodeCommand();
            cmd.Parameters["@ino"].Value = ino;
            cmd.Parameters["@kind"].Value = (int)kind;
            cmd.Parameters["@mode"].Value = mode;
            cmd.Parameters["@uid"].Value = uid;
            cmd.Parameters["@gid"].Value = gid;
            cmd.Parameters["@nlink"].Value = nlink;
            cmd.Parameters["@rdev"].Value = (long)rdev;
            cmd.Parameters["@size"].Value = size;
            cmd.Parameters["@atime"].Value = effectiveAtime;
            cmd.Parameters["@mtime"].Value = effectiveMtime;
            cmd.Parameters["@ctime"].Value = effectiveCtime;
            ExecuteNonQuery(cmd);
        }

        public void UpsertDentry(long parentIno, string name, long ino)
        {
            var cmd = _session.GetUpsertDentryCommand();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = name;
            cmd.Parameters["@i"].Value = ino;
            ExecuteNonQuery(cmd);
        }

        public void RemoveDentry(long parentIno, string name)
        {
            var cmd = _session.GetRemoveDentryCommand();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = name;
            ExecuteNonQuery(cmd);
        }

        public void MarkWhiteout(long parentIno, string name)
        {
            var cmd = _session.GetMarkWhiteoutCommand();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = name;
            ExecuteNonQuery(cmd);
        }

        public void ClearWhiteout(long parentIno, string name)
        {
            var cmd = _session.GetClearWhiteoutCommand();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = name;
            ExecuteNonQuery(cmd);
        }

        public void MarkOpaque(long parentIno)
        {
            var cmd = _session.GetMarkOpaqueCommand();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = OpaqueMarkerName;
            ExecuteNonQuery(cmd);
        }

        private void ExecuteNonQuery(SqliteCommand cmd)
        {
            cmd.Transaction = _tx;
            try
            {
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Transaction = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
        }
    }
}

public sealed class SilkMetadataSession : IDisposable
{
    private readonly SqliteConnection _conn;
    private SqliteCommand? _clearOpaqueCmd;
    private SqliteCommand? _clearWhiteoutCmd;
    private SqliteCommand? _createInodeCmd;
    private SqliteCommand? _deleteInodeCmd;
    private SqliteCommand? _getInodeCmd;
    private SqliteCommand? _getXAttrCmd;
    private SqliteCommand? _hasWhiteoutCmd;
    private SqliteCommand? _inodeExistsCmd;
    private SqliteCommand? _isOpaqueCmd;
    private SqliteCommand? _listDentriesByParentCmd;
    private SqliteCommand? _listDentriesCmd;
    private SqliteCommand? _listInodesCmd;
    private SqliteCommand? _listOrphanInodesCmd;
    private SqliteCommand? _listXAttrsCmd;
    private SqliteCommand? _lookupDentryCmd;
    private SqliteCommand? _markOpaqueCmd;
    private SqliteCommand? _markWhiteoutCmd;
    private SqliteCommand? _removeXAttrCmd;
    private SqliteCommand? _removeDentryCmd;
    private SqliteCommand? _setXAttrCmd;
    private SqliteCommand? _upsertDentryCmd;
    private SqliteCommand? _upsertInodeCmd;
    private bool _disposed;

    internal SilkMetadataSession(SilkMetadataStore store, SqliteConnection connection)
    {
        Store = store;
        _conn = connection;
    }

    public SilkMetadataStore Store { get; }

    public long CreateInode(SilkInodeKind kind, int mode, int uid = 0, int gid = 0, uint rdev = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        _createInodeCmd ??= PrepareCommand(
            "INSERT INTO inodes(kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns) VALUES (@kind, @mode, @uid, @gid, 1, @rdev, 0, @now, @now, @now); SELECT last_insert_rowid();",
            cmd =>
            {
                cmd.Parameters.Add("@kind", SqliteType.Integer);
                cmd.Parameters.Add("@mode", SqliteType.Integer);
                cmd.Parameters.Add("@uid", SqliteType.Integer);
                cmd.Parameters.Add("@gid", SqliteType.Integer);
                cmd.Parameters.Add("@rdev", SqliteType.Integer);
                cmd.Parameters.Add("@now", SqliteType.Integer);
            });
        _createInodeCmd.Parameters["@kind"].Value = (int)kind;
        _createInodeCmd.Parameters["@mode"].Value = mode;
        _createInodeCmd.Parameters["@uid"].Value = uid;
        _createInodeCmd.Parameters["@gid"].Value = gid;
        _createInodeCmd.Parameters["@rdev"].Value = (long)rdev;
        _createInodeCmd.Parameters["@now"].Value = now;
        return Convert.ToInt64(_createInodeCmd.ExecuteScalar());
    }

    public void ExecuteTransaction(Action<SilkMetadataStore.SilkMetadataTransaction> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var tx = _conn.BeginTransaction();
        using var metadataTx = new SilkMetadataStore.SilkMetadataTransaction(this, tx);
        action(metadataTx);
        tx.Commit();
    }

    public bool InodeExists(long ino)
    {
        _inodeExistsCmd ??= PrepareCommand(
            "SELECT COUNT(1) FROM inodes WHERE ino = @ino;",
            cmd => cmd.Parameters.Add("@ino", SqliteType.Integer));
        _inodeExistsCmd.Parameters["@ino"].Value = ino;
        return Convert.ToInt64(_inodeExistsCmd.ExecuteScalar()) > 0;
    }

    public SilkInodeRecord? GetInode(long ino)
    {
        _getInodeCmd ??= PrepareCommand(
            "SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns FROM inodes WHERE ino = @ino;",
            cmd => cmd.Parameters.Add("@ino", SqliteType.Integer));
        _getInodeCmd.Parameters["@ino"].Value = ino;
        using var reader = _getInodeCmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadInode(reader);
    }

    public List<SilkInodeRecord> ListInodes()
    {
        _listInodesCmd ??= PrepareCommand(
            "SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns FROM inodes ORDER BY ino ASC;",
            _ => { });
        using var reader = _listInodesCmd.ExecuteReader();
        var result = new List<SilkInodeRecord>();
        while (reader.Read())
            result.Add(ReadInode(reader));
        return result;
    }

    public List<long> ListOrphanInodes()
    {
        _listOrphanInodesCmd ??= PrepareCommand(
            "SELECT ino FROM inodes WHERE ino <> @root AND nlink <= 0 ORDER BY ino ASC;",
            cmd => cmd.Parameters.Add("@root", SqliteType.Integer));
        _listOrphanInodesCmd.Parameters["@root"].Value = SilkMetadataStore.RootInode;
        using var reader = _listOrphanInodesCmd.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
            result.Add(reader.GetInt64(0));
        return result;
    }

    public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
        long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
    {
        ExecuteTransaction(tx => tx.UpsertInode(ino, kind, mode, uid, gid, nlink, rdev, size, atimeNs, mtimeNs, ctimeNs));
    }

    public void UpsertDentry(long parentIno, string name, long ino)
    {
        ExecuteTransaction(tx => tx.UpsertDentry(parentIno, name, ino));
    }

    public long? LookupDentry(long parentIno, string name)
    {
        _lookupDentryCmd ??= PrepareCommand(
            "SELECT ino FROM dentries WHERE parent_ino = @p AND name = @n;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
        _lookupDentryCmd.Parameters["@p"].Value = parentIno;
        _lookupDentryCmd.Parameters["@n"].Value = name;
        var value = _lookupDentryCmd.ExecuteScalar();
        return value == null || value is DBNull ? null : Convert.ToInt64(value);
    }

    public List<SilkDentryRecord> ListDentries()
    {
        _listDentriesCmd ??= PrepareCommand(
            "SELECT parent_ino, name, ino FROM dentries ORDER BY parent_ino ASC, name ASC;",
            _ => { });
        using var reader = _listDentriesCmd.ExecuteReader();
        return ReadDentries(reader);
    }

    public List<SilkDentryRecord> ListDentriesByParent(long parentIno)
    {
        _listDentriesByParentCmd ??= PrepareCommand(
            "SELECT parent_ino, name, ino FROM dentries WHERE parent_ino = @p ORDER BY name ASC;",
            cmd => cmd.Parameters.Add("@p", SqliteType.Integer));
        _listDentriesByParentCmd.Parameters["@p"].Value = parentIno;
        using var reader = _listDentriesByParentCmd.ExecuteReader();
        return ReadDentries(reader);
    }

    public void RemoveDentry(long parentIno, string name)
    {
        ExecuteTransaction(tx => tx.RemoveDentry(parentIno, name));
    }

    public void DeleteInode(long ino)
    {
        if (ino == SilkMetadataStore.RootInode) return;
        _deleteInodeCmd ??= PrepareCommand(
            "DELETE FROM inodes WHERE ino = @ino;",
            cmd => cmd.Parameters.Add("@ino", SqliteType.Integer));
        _deleteInodeCmd.Parameters["@ino"].Value = ino;
        _deleteInodeCmd.ExecuteNonQuery();
    }

    public void SetXAttr(long ino, string key, ReadOnlySpan<byte> value)
    {
        _setXAttrCmd ??= PrepareCommand(
            "INSERT INTO xattrs(ino, key, value) VALUES (@ino, @key, @value) ON CONFLICT(ino, key) DO UPDATE SET value = excluded.value;",
            cmd =>
            {
                cmd.Parameters.Add("@ino", SqliteType.Integer);
                cmd.Parameters.Add("@key", SqliteType.Text);
                cmd.Parameters.Add("@value", SqliteType.Blob);
            });
        _setXAttrCmd.Parameters["@ino"].Value = ino;
        _setXAttrCmd.Parameters["@key"].Value = key;
        _setXAttrCmd.Parameters["@value"].Value = value.ToArray();
        _setXAttrCmd.ExecuteNonQuery();
    }

    public byte[]? GetXAttr(long ino, string key)
    {
        _getXAttrCmd ??= PrepareCommand(
            "SELECT value FROM xattrs WHERE ino = @ino AND key = @key;",
            cmd =>
            {
                cmd.Parameters.Add("@ino", SqliteType.Integer);
                cmd.Parameters.Add("@key", SqliteType.Text);
            });
        _getXAttrCmd.Parameters["@ino"].Value = ino;
        _getXAttrCmd.Parameters["@key"].Value = key;
        var value = _getXAttrCmd.ExecuteScalar();
        if (value == null || value is DBNull) return null;
        return (byte[])value;
    }

    public Dictionary<string, byte[]> ListXAttrs(long ino)
    {
        _listXAttrsCmd ??= PrepareCommand(
            "SELECT key, value FROM xattrs WHERE ino = @ino ORDER BY key ASC;",
            cmd => cmd.Parameters.Add("@ino", SqliteType.Integer));
        _listXAttrsCmd.Parameters["@ino"].Value = ino;
        using var reader = _listXAttrsCmd.ExecuteReader();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = (byte[])reader["value"];
        return result;
    }

    public void RemoveXAttr(long ino, string key)
    {
        _removeXAttrCmd ??= PrepareCommand(
            "DELETE FROM xattrs WHERE ino = @ino AND key = @key;",
            cmd =>
            {
                cmd.Parameters.Add("@ino", SqliteType.Integer);
                cmd.Parameters.Add("@key", SqliteType.Text);
            });
        _removeXAttrCmd.Parameters["@ino"].Value = ino;
        _removeXAttrCmd.Parameters["@key"].Value = key;
        _removeXAttrCmd.ExecuteNonQuery();
    }

    public void MarkWhiteout(long parentIno, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Whiteout name cannot be empty.", nameof(name));
        ExecuteTransaction(tx => tx.MarkWhiteout(parentIno, name));
    }

    public bool HasWhiteout(long parentIno, string name)
    {
        _hasWhiteoutCmd ??= PrepareCommand(
            "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 0;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
        _hasWhiteoutCmd.Parameters["@p"].Value = parentIno;
        _hasWhiteoutCmd.Parameters["@n"].Value = name;
        return Convert.ToInt64(_hasWhiteoutCmd.ExecuteScalar()) > 0;
    }

    public void ClearWhiteout(long parentIno, string name)
    {
        ExecuteTransaction(tx => tx.ClearWhiteout(parentIno, name));
    }

    public void MarkOpaque(long parentIno)
    {
        ExecuteTransaction(tx => tx.MarkOpaque(parentIno));
    }

    public bool IsOpaque(long parentIno)
    {
        _isOpaqueCmd ??= PrepareCommand(
            "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 1;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
        _isOpaqueCmd.Parameters["@p"].Value = parentIno;
        _isOpaqueCmd.Parameters["@n"].Value = SilkMetadataStore.OpaqueMarkerName;
        return Convert.ToInt64(_isOpaqueCmd.ExecuteScalar()) > 0;
    }

    public void ClearOpaque(long parentIno)
    {
        _clearOpaqueCmd ??= PrepareCommand(
            "DELETE FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 1;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
        _clearOpaqueCmd.Parameters["@p"].Value = parentIno;
        _clearOpaqueCmd.Parameters["@n"].Value = SilkMetadataStore.OpaqueMarkerName;
        _clearOpaqueCmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        DisposeCommand(ref _clearOpaqueCmd);
        DisposeCommand(ref _clearWhiteoutCmd);
        DisposeCommand(ref _createInodeCmd);
        DisposeCommand(ref _deleteInodeCmd);
        DisposeCommand(ref _getInodeCmd);
        DisposeCommand(ref _getXAttrCmd);
        DisposeCommand(ref _hasWhiteoutCmd);
        DisposeCommand(ref _inodeExistsCmd);
        DisposeCommand(ref _isOpaqueCmd);
        DisposeCommand(ref _listDentriesByParentCmd);
        DisposeCommand(ref _listDentriesCmd);
        DisposeCommand(ref _listInodesCmd);
        DisposeCommand(ref _listOrphanInodesCmd);
        DisposeCommand(ref _listXAttrsCmd);
        DisposeCommand(ref _lookupDentryCmd);
        DisposeCommand(ref _markOpaqueCmd);
        DisposeCommand(ref _markWhiteoutCmd);
        DisposeCommand(ref _removeXAttrCmd);
        DisposeCommand(ref _removeDentryCmd);
        DisposeCommand(ref _setXAttrCmd);
        DisposeCommand(ref _upsertDentryCmd);
        DisposeCommand(ref _upsertInodeCmd);
        _conn.Dispose();
        _disposed = true;
    }

    private SqliteCommand PrepareCommand(string sql, Action<SqliteCommand> configureParameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        configureParameters(cmd);
        cmd.Prepare();
        return cmd;
    }

    internal SqliteCommand GetClearWhiteoutCommand()
    {
        return _clearWhiteoutCmd ??= PrepareCommand(
            "DELETE FROM whiteouts WHERE parent_ino = @p AND name = @n;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
    }

    internal SqliteCommand GetMarkOpaqueCommand()
    {
        return _markOpaqueCmd ??= PrepareCommand(
            "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 1) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 1;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
    }

    internal SqliteCommand GetMarkWhiteoutCommand()
    {
        return _markWhiteoutCmd ??= PrepareCommand(
            "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 0) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 0;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
    }

    internal SqliteCommand GetRemoveDentryCommand()
    {
        return _removeDentryCmd ??= PrepareCommand(
            "DELETE FROM dentries WHERE parent_ino = @p AND name = @n;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
            });
    }

    internal SqliteCommand GetUpsertDentryCommand()
    {
        return _upsertDentryCmd ??= PrepareCommand(
            "INSERT INTO dentries(parent_ino, name, ino) VALUES (@p, @n, @i) ON CONFLICT(parent_ino, name) DO UPDATE SET ino = excluded.ino;",
            cmd =>
            {
                cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.Add("@n", SqliteType.Text);
                cmd.Parameters.Add("@i", SqliteType.Integer);
            });
    }

    internal SqliteCommand GetUpsertInodeCommand()
    {
        return _upsertInodeCmd ??= PrepareCommand("""
                                                  INSERT INTO inodes(ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns)
                                                  VALUES (@ino, @kind, @mode, @uid, @gid, @nlink, @rdev, @size, @atime, @mtime, @ctime)
                                                  ON CONFLICT(ino) DO UPDATE SET
                                                    kind = excluded.kind,
                                                    mode = excluded.mode,
                                                    uid = excluded.uid,
                                                    gid = excluded.gid,
                                                    nlink = excluded.nlink,
                                                    rdev = excluded.rdev,
                                                    size = excluded.size,
                                                    atime_ns = excluded.atime_ns,
                                                    mtime_ns = excluded.mtime_ns,
                                                    ctime_ns = excluded.ctime_ns;
                                                  """,
            cmd =>
            {
                cmd.Parameters.Add("@ino", SqliteType.Integer);
                cmd.Parameters.Add("@kind", SqliteType.Integer);
                cmd.Parameters.Add("@mode", SqliteType.Integer);
                cmd.Parameters.Add("@uid", SqliteType.Integer);
                cmd.Parameters.Add("@gid", SqliteType.Integer);
                cmd.Parameters.Add("@nlink", SqliteType.Integer);
                cmd.Parameters.Add("@rdev", SqliteType.Integer);
                cmd.Parameters.Add("@size", SqliteType.Integer);
                cmd.Parameters.Add("@atime", SqliteType.Integer);
                cmd.Parameters.Add("@mtime", SqliteType.Integer);
                cmd.Parameters.Add("@ctime", SqliteType.Integer);
            });
    }

    private static void DisposeCommand(ref SqliteCommand? cmd)
    {
        cmd?.Dispose();
        cmd = null;
    }

    private static SilkInodeRecord ReadInode(SqliteDataReader reader)
    {
        return new SilkInodeRecord(
            reader.GetInt64(0),
            (SilkInodeKind)reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10));
    }

    private static List<SilkDentryRecord> ReadDentries(SqliteDataReader reader)
    {
        var result = new List<SilkDentryRecord>();
        while (reader.Read())
            result.Add(new SilkDentryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }
}
