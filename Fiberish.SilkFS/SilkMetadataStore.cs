using Microsoft.Data.Sqlite;
using System.Threading;

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
    long Size);

public readonly record struct SilkDentryRecord(
    long ParentIno,
    string Name,
    long Ino);

public readonly record struct SilkObjectBindingResult(
    bool Changed,
    string? UnreferencedObjectId);

public sealed class SilkMetadataStore
{
    public const long RootInode = 1;
    public const string OpaqueMarkerName = ".wh..wh..opq";
    private readonly string _connectionString;

    public SilkMetadataStore(string dbPath)
    {
        EnsureSqliteProviderInitialized();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public sealed class SilkMetadataTransaction
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;

        internal SilkMetadataTransaction(SqliteConnection conn, SqliteTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
            long size = 0)
        {
            UpsertInodeCore(_conn, _tx, ino, kind, mode, uid, gid, nlink, rdev, size);
        }

        public void UpsertDentry(long parentIno, string name, long ino)
        {
            UpsertDentryCore(_conn, _tx, parentIno, name, ino);
        }

        public void RemoveDentry(long parentIno, string name)
        {
            RemoveDentryCore(_conn, _tx, parentIno, name);
        }

        public void MarkWhiteout(long parentIno, string name)
        {
            MarkWhiteoutCore(_conn, _tx, parentIno, name);
        }

        public void ClearWhiteout(long parentIno, string name)
        {
            ClearWhiteoutCore(_conn, _tx, parentIno, name);
        }

        public void MarkOpaque(long parentIno)
        {
            MarkOpaqueCore(_conn, _tx, parentIno);
        }
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
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS inode_objects (
                         ino INTEGER PRIMARY KEY,
                         object_id TEXT NOT NULL,
                         FOREIGN KEY (ino) REFERENCES inodes(ino) ON DELETE CASCADE
                       );
                       """);
        Exec(conn, tx, """
                       CREATE TABLE IF NOT EXISTS objects (
                         object_id TEXT PRIMARY KEY,
                         refcount INTEGER NOT NULL
                       );
                       """);
        Exec(conn, tx, """
                       INSERT INTO objects(object_id, refcount)
                       SELECT io.object_id, COUNT(1)
                       FROM inode_objects io
                       GROUP BY io.object_id
                       ON CONFLICT(object_id) DO UPDATE SET refcount = excluded.refcount;
                       """);
        Exec(conn, tx, "INSERT OR IGNORE INTO meta(k, v) VALUES ('schema_version', '1');");

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
        long size = 0)
    {
        ExecuteTransaction(tx => tx.UpsertInode(ino, kind, mode, uid, gid, nlink, rdev, size));
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
        cmd.CommandText = "SELECT ino, kind, mode, uid, gid, nlink, rdev, size FROM inodes WHERE ino = @ino;";
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
            reader.GetInt64(7));
    }

    public List<SilkInodeRecord> ListInodes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ino, kind, mode, uid, gid, nlink, rdev, size FROM inodes ORDER BY ino ASC;";
        using var reader = cmd.ExecuteReader();
        var result = new List<SilkInodeRecord>();
        while (reader.Read())
        {
            result.Add(new SilkInodeRecord(
                reader.GetInt64(0),
                (SilkInodeKind)reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

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
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ino FROM dentries WHERE parent_ino = @p AND name = @n;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        cmd.Parameters.AddWithValue("@n", name);
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : Convert.ToInt64(value);
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
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parent_ino, name, ino FROM dentries WHERE parent_ino = @p ORDER BY name ASC;";
        cmd.Parameters.AddWithValue("@p", parentIno);
        using var reader = cmd.ExecuteReader();
        var result = new List<SilkDentryRecord>();
        while (reader.Read())
            result.Add(new SilkDentryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
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

    public string? DeleteInodeWithObjectRefCount(long ino)
    {
        if (ino == RootInode) return null;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var oldObjectId = GetInodeObject(conn, tx, ino);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM inodes WHERE ino = @ino;";
            cmd.Parameters.AddWithValue("@ino", ino);
            cmd.ExecuteNonQuery();
        }

        string? unreferenced = null;
        if (!string.IsNullOrEmpty(oldObjectId))
        {
            if (DecrementObjectRef(conn, tx, oldObjectId!))
                unreferenced = oldObjectId;
        }

        tx.Commit();
        return unreferenced;
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

    public void SetInodeObject(long ino, string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("Object id cannot be empty.", nameof(objectId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO inode_objects(ino, object_id) VALUES (@ino, @obj) ON CONFLICT(ino) DO UPDATE SET object_id = excluded.object_id;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.Parameters.AddWithValue("@obj", objectId);
        cmd.ExecuteNonQuery();
    }

    public SilkObjectBindingResult SetInodeObjectWithRefCount(long ino, string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("Object id cannot be empty.", nameof(objectId));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var current = GetInodeObject(conn, tx, ino);
        if (string.Equals(current, objectId, StringComparison.Ordinal))
        {
            tx.Commit();
            return new SilkObjectBindingResult(false, null);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO inode_objects(ino, object_id) VALUES (@ino, @obj) ON CONFLICT(ino) DO UPDATE SET object_id = excluded.object_id;";
            cmd.Parameters.AddWithValue("@ino", ino);
            cmd.Parameters.AddWithValue("@obj", objectId);
            cmd.ExecuteNonQuery();
        }

        IncrementObjectRef(conn, tx, objectId);

        string? unreferenced = null;
        if (!string.IsNullOrEmpty(current))
        {
            if (DecrementObjectRef(conn, tx, current!))
                unreferenced = current;
        }

        tx.Commit();
        return new SilkObjectBindingResult(true, unreferenced);
    }

    public string? GetInodeObject(long ino)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT object_id FROM inode_objects WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : (string)value;
    }

    public void RemoveInodeObject(long ino)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM inode_objects WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        cmd.ExecuteNonQuery();
    }

    public long GetObjectRefCount(string objectId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT refcount FROM objects WHERE object_id = @obj;";
        cmd.Parameters.AddWithValue("@obj", objectId);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
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

    private static int _sqliteInit;

    private static void EnsureSqliteProviderInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInit, 1) != 0)
            return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
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

    private static void IncrementObjectRef(SqliteConnection conn, SqliteTransaction tx, string objectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          INSERT INTO objects(object_id, refcount) VALUES (@obj, 1)
                          ON CONFLICT(object_id) DO UPDATE SET refcount = refcount + 1;
                          """;
        cmd.Parameters.AddWithValue("@obj", objectId);
        cmd.ExecuteNonQuery();
    }

    private static bool DecrementObjectRef(SqliteConnection conn, SqliteTransaction tx, string objectId)
    {
        long current;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT refcount FROM objects WHERE object_id = @obj;";
            cmd.Parameters.AddWithValue("@obj", objectId);
            var v = cmd.ExecuteScalar();
            current = v == null || v is DBNull ? 0 : Convert.ToInt64(v);
        }

        if (current <= 1)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM objects WHERE object_id = @obj;";
            del.Parameters.AddWithValue("@obj", objectId);
            del.ExecuteNonQuery();
            return true;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE objects SET refcount = refcount - 1 WHERE object_id = @obj;";
            cmd.Parameters.AddWithValue("@obj", objectId);
            cmd.ExecuteNonQuery();
        }

        return false;
    }

    private static string? GetInodeObject(SqliteConnection conn, SqliteTransaction tx, long ino)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT object_id FROM inode_objects WHERE ino = @ino;";
        cmd.Parameters.AddWithValue("@ino", ino);
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : (string)value;
    }

    private static void UpsertInodeCore(SqliteConnection conn, SqliteTransaction tx, long ino, SilkInodeKind kind, int mode,
        int uid, int gid, int nlink, uint rdev, long size)
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

    private static void UpsertDentryCore(SqliteConnection conn, SqliteTransaction tx, long parentIno, string name, long ino)
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
}
