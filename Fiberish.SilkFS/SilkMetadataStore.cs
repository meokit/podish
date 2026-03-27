using System.Data;
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
    private readonly Lock _readLock = new();
    private SqliteCommand? _listDentriesByParentCmd;
    private SqliteCommand? _lookupDentryCmd;
    private SqliteConnection? _readConnection;

    public SilkMetadataStore(string dbPath)
    {
        EnsureSqliteProviderInitialized();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public void ExecuteTransaction(Action<SilkMetadataTransaction> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        action(new SilkMetadataTransaction(conn, tx));
        tx.Commit();
    }

    public void Initialize()
    {
        using var conn = OpenConnection();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
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

    public long CreateInode(SilkInodeKind kind, int mode, int uid = 0, int gid = 0, uint rdev = 0)
    {
        using var conn = OpenConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO inodes(kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns) VALUES (@kind, @mode, @uid, @gid, 1, @rdev, 0, @now, @now, @now); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@kind", (int)kind);
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.Parameters.AddWithValue("@gid", gid);
        cmd.Parameters.AddWithValue("@rdev", (long)rdev);
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
        long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
    {
        ExecuteTransaction(tx =>
            tx.UpsertInode(ino, kind, mode, uid, gid, nlink, rdev, size, atimeNs, mtimeNs, ctimeNs));
    }

    public bool InodeExists(long ino)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM inodes WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public SilkInodeRecord? GetInode(long ino)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns FROM inodes WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
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

    public List<SilkInodeRecord> ListInodes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns FROM inodes ORDER BY ino ASC;";
        using var reader = cmd.ExecuteReader();
        var result = new List<SilkInodeRecord>();
        while (reader.Read())
            result.Add(new SilkInodeRecord(
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
                reader.GetInt64(10)));

        return result;
    }

    public List<long> ListOrphanInodes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ino FROM inodes WHERE ino <> @root AND nlink <= 0 ORDER BY ino ASC;";
        cmd.Parameters.AddWithValue("@root", RootInode);
        using var reader = cmd.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
            result.Add(reader.GetInt64(0));
        return result;
    }

    public void UpsertDentry(long parentIno, string name, long ino)
    {
        ExecuteTransaction(tx => tx.UpsertDentry(parentIno, name, ino));
    }

    public long? LookupDentry(long parentIno, string name)
    {
        lock (_readLock)
        {
            var cmd = GetLookupDentryCommandLocked();
            cmd.Parameters["@p"].Value = parentIno;
            cmd.Parameters["@n"].Value = name;
            var value = cmd.ExecuteScalar();
            return value == null || value is DBNull ? null : Convert.ToInt64(value);
        }
    }

    public List<SilkDentryRecord> ListDentries()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parent_ino, name, ino FROM dentries ORDER BY parent_ino ASC, name ASC;";
        using var reader = cmd.ExecuteReader();
        var result = new List<SilkDentryRecord>();
        while (reader.Read())
            result.Add(new SilkDentryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    public List<SilkDentryRecord> ListDentriesByParent(long parentIno)
    {
        lock (_readLock)
        {
            var cmd = GetListDentriesByParentCommandLocked();
            cmd.Parameters["@p"].Value = parentIno;
            using var reader = cmd.ExecuteReader();
            var result = new List<SilkDentryRecord>();
            while (reader.Read())
                result.Add(new SilkDentryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
            return result;
        }
    }

    public void RemoveDentry(long parentIno, string name)
    {
        ExecuteTransaction(tx => tx.RemoveDentry(parentIno, name));
    }

    public void DeleteInode(long ino)
    {
        if (ino == RootInode) return;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM inodes WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.ExecuteNonQuery();
    }

    public void SetXAttr(long ino, string key, ReadOnlySpan<byte> value)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO xattrs(ino, key, value) VALUES (@ino, @key, @value) ON CONFLICT(ino, key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.Add("@value", SqliteType.Blob).Value = value.ToArray();
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetXAttr(long ino, string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM xattrs WHERE ino = @ino AND key = @key;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.Parameters.AddWithValue("@key", key);
        var value = cmd.ExecuteScalar();
        if (value == null || value is DBNull) return null;
        return (byte[])value;
    }

    public Dictionary<string, byte[]> ListXAttrs(long ino)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM xattrs WHERE ino = @ino ORDER BY key ASC;";
        cmd.Parameters.AddWithValue("@ino", ino);
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = (byte[])reader["value"];
        return result;
    }

    public void RemoveXAttr(long ino, string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM xattrs WHERE ino = @ino AND key = @key;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }

    public void MarkWhiteout(long parentIno, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Whiteout name cannot be empty.", nameof(name));
        ExecuteTransaction(tx => tx.MarkWhiteout(parentIno, name));
    }

    public bool HasWhiteout(long parentIno, string name)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 0;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
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
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 1;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", OpaqueMarkerName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void ClearOpaque(long parentIno)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM whiteouts WHERE parent_ino = @p AND name = @n AND opaque = 1;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", OpaqueMarkerName);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
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

    private static void UpsertInodeCore(SqliteConnection conn, SqliteTransaction tx, long ino, SilkInodeKind kind,
        int mode,
        int uid, int gid, int nlink, uint rdev, long size, long? atimeNs = null, long? mtimeNs = null,
        long? ctimeNs = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          INSERT INTO inodes(ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns)
                          VALUES (@ino, @kind, @mode, @uid, @gid, @nlink, @rdev, @size, @now, @now, @now)
                          ON CONFLICT(ino) DO UPDATE SET
                            kind = excluded.kind,
                            mode = excluded.mode,
                            uid = excluded.uid,
                            gid = excluded.gid,
                            nlink = excluded.nlink,
                            rdev = excluded.rdev,
                            size = excluded.size,
                            mtime_ns = excluded.mtime_ns,
                            ctime_ns = excluded.ctime_ns;
                          """;
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.Parameters.AddWithValue("@kind", (int)kind);
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.Parameters.AddWithValue("@gid", gid);
        cmd.Parameters.AddWithValue("@nlink", nlink);
        cmd.Parameters.AddWithValue("@rdev", (long)rdev);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertDentryCore(SqliteConnection conn, SqliteTransaction tx, long parentIno, string name,
        long ino)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO dentries(parent_ino, name, ino) VALUES (@p, @n, @i) ON CONFLICT(parent_ino, name) DO UPDATE SET ino = excluded.ino;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@i", ino);
        cmd.ExecuteNonQuery();
    }

    private static void RemoveDentryCore(SqliteConnection conn, SqliteTransaction tx, long parentIno, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM dentries WHERE parent_ino = @p AND name = @n;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.ExecuteNonQuery();
    }

    private static void MarkWhiteoutCore(SqliteConnection conn, SqliteTransaction tx, long parentIno, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 0) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 0;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.ExecuteNonQuery();
    }

    private static void ClearWhiteoutCore(SqliteConnection conn, SqliteTransaction tx, long parentIno, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM whiteouts WHERE parent_ino = @p AND name = @n;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.ExecuteNonQuery();
    }

    private static void MarkOpaqueCore(SqliteConnection conn, SqliteTransaction tx, long parentIno)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 1) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 1;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", OpaqueMarkerName);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection GetReadConnectionLocked()
    {
        if (_readConnection is { State: ConnectionState.Open })
            return _readConnection;

        _readConnection?.Dispose();
        _readConnection = OpenConnection();
        _lookupDentryCmd = null;
        _listDentriesByParentCmd = null;
        return _readConnection;
    }

    private SqliteCommand GetLookupDentryCommandLocked()
    {
        if (_lookupDentryCmd != null) return _lookupDentryCmd;
        var cmd = GetReadConnectionLocked().CreateCommand();
        cmd.CommandText = "SELECT ino FROM dentries WHERE parent_ino = @p AND name = @n;";
        cmd.Parameters.Add("@p", SqliteType.Integer);
        cmd.Parameters.Add("@n", SqliteType.Text);
        _lookupDentryCmd = cmd;
        return cmd;
    }

    private SqliteCommand GetListDentriesByParentCommandLocked()
    {
        if (_listDentriesByParentCmd != null) return _listDentriesByParentCmd;
        var cmd = GetReadConnectionLocked().CreateCommand();
        cmd.CommandText = "SELECT parent_ino, name, ino FROM dentries WHERE parent_ino = @p ORDER BY name ASC;";
        cmd.Parameters.Add("@p", SqliteType.Integer);
        _listDentriesByParentCmd = cmd;
        return cmd;
    }

    public sealed class SilkMetadataTransaction
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;
        private SqliteCommand? _clearWhiteoutCmd;
        private SqliteCommand? _markOpaqueCmd;
        private SqliteCommand? _markWhiteoutCmd;
        private SqliteCommand? _removeDentryCmd;
        private SqliteCommand? _upsertDentryCmd;
        private SqliteCommand? _upsertInodeCmd;

        internal SilkMetadataTransaction(SqliteConnection conn, SqliteTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
            long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var effectiveAtime = atimeNs ?? now;
            var effectiveMtime = mtimeNs ?? now;
            var effectiveCtime = ctimeNs ?? now;
            _upsertInodeCmd ??= PrepareCommand("""
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
            _upsertInodeCmd.Parameters["@ino"].Value = ino;
            _upsertInodeCmd.Parameters["@kind"].Value = (int)kind;
            _upsertInodeCmd.Parameters["@mode"].Value = mode;
            _upsertInodeCmd.Parameters["@uid"].Value = uid;
            _upsertInodeCmd.Parameters["@gid"].Value = gid;
            _upsertInodeCmd.Parameters["@nlink"].Value = nlink;
            _upsertInodeCmd.Parameters["@rdev"].Value = (long)rdev;
            _upsertInodeCmd.Parameters["@size"].Value = size;
            _upsertInodeCmd.Parameters["@atime"].Value = effectiveAtime;
            _upsertInodeCmd.Parameters["@mtime"].Value = effectiveMtime;
            _upsertInodeCmd.Parameters["@ctime"].Value = effectiveCtime;
            _upsertInodeCmd.ExecuteNonQuery();
        }

        public void UpsertDentry(long parentIno, string name, long ino)
        {
            _upsertDentryCmd ??= PrepareCommand(
                "INSERT INTO dentries(parent_ino, name, ino) VALUES (@p, @n, @i) ON CONFLICT(parent_ino, name) DO UPDATE SET ino = excluded.ino;",
                cmd =>
                {
                    cmd.Parameters.Add("@p", SqliteType.Integer);
                    cmd.Parameters.Add("@n", SqliteType.Text);
                    cmd.Parameters.Add("@i", SqliteType.Integer);
                });
            _upsertDentryCmd.Parameters["@p"].Value = parentIno;
            _upsertDentryCmd.Parameters["@n"].Value = name;
            _upsertDentryCmd.Parameters["@i"].Value = ino;
            _upsertDentryCmd.ExecuteNonQuery();
        }

        public void RemoveDentry(long parentIno, string name)
        {
            _removeDentryCmd ??= PrepareCommand(
                "DELETE FROM dentries WHERE parent_ino = @p AND name = @n;",
                cmd =>
                {
                    cmd.Parameters.Add("@p", SqliteType.Integer);
                    cmd.Parameters.Add("@n", SqliteType.Text);
                });
            _removeDentryCmd.Parameters["@p"].Value = parentIno;
            _removeDentryCmd.Parameters["@n"].Value = name;
            _removeDentryCmd.ExecuteNonQuery();
        }

        public void MarkWhiteout(long parentIno, string name)
        {
            _markWhiteoutCmd ??= PrepareCommand(
                "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 0) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 0;",
                cmd =>
                {
                    cmd.Parameters.Add("@p", SqliteType.Integer);
                    cmd.Parameters.Add("@n", SqliteType.Text);
                });
            _markWhiteoutCmd.Parameters["@p"].Value = parentIno;
            _markWhiteoutCmd.Parameters["@n"].Value = name;
            _markWhiteoutCmd.ExecuteNonQuery();
        }

        public void ClearWhiteout(long parentIno, string name)
        {
            _clearWhiteoutCmd ??= PrepareCommand(
                "DELETE FROM whiteouts WHERE parent_ino = @p AND name = @n;",
                cmd =>
                {
                    cmd.Parameters.Add("@p", SqliteType.Integer);
                    cmd.Parameters.Add("@n", SqliteType.Text);
                });
            _clearWhiteoutCmd.Parameters["@p"].Value = parentIno;
            _clearWhiteoutCmd.Parameters["@n"].Value = name;
            _clearWhiteoutCmd.ExecuteNonQuery();
        }

        public void MarkOpaque(long parentIno)
        {
            _markOpaqueCmd ??= PrepareCommand(
                "INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (@p, @n, 1) ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 1;",
                cmd =>
                {
                    cmd.Parameters.Add("@p", SqliteType.Integer);
                    cmd.Parameters.Add("@n", SqliteType.Text);
                });
            _markOpaqueCmd.Parameters["@p"].Value = parentIno;
            _markOpaqueCmd.Parameters["@n"].Value = OpaqueMarkerName;
            _markOpaqueCmd.ExecuteNonQuery();
        }

        private SqliteCommand PrepareCommand(string sql, Action<SqliteCommand> configureParameters)
        {
            var cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            configureParameters(cmd);
            return cmd;
        }
    }
}