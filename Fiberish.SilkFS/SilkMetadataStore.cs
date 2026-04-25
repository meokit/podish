using SQLitePCL;
using System.Text;

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
    byte[] Name,
    long Ino);

public readonly record struct SilkXAttrRecord(
    byte[] Key,
    byte[] Value);

public sealed class SilkMetadataStore
{
    public const long RootInode = 1;
    public const int CurrentSchemaVersion = 3;
    public static ReadOnlySpan<byte> OpaqueMarkerNameBytes => ".wh..wh..opq"u8;

    private static readonly byte[] SchemaVersionUtf8 = "3"u8.ToArray();
    private static int _sqliteInit;
    private readonly string _dbPath;

    public SilkMetadataStore(string dbPath)
    {
        EnsureSqliteProviderInitialized();
        _dbPath = dbPath;
    }

    public SilkMetadataSession OpenSession()
    {
        return new SilkMetadataSession(this, OpenConnection());
    }

    public void Initialize()
    {
        using var conn = OpenConnection();
        conn.ExecuteNonQuery(SilkMetadataSql.PragmaJournalModeWal);
        using var tx = conn.BeginTransaction();

        conn.ExecuteNonQuery(SilkMetadataSql.CreateMetaTable);
        var schemaVersion = TryReadSchemaVersion(conn);
        if (schemaVersion != null && schemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"SilkFS metadata schema v{schemaVersion} is unsupported; expected v{CurrentSchemaVersion}. Rebuild or migrate the repo.");

        conn.ExecuteNonQuery(SilkMetadataSql.CreateInodesTable);
        conn.ExecuteNonQuery(SilkMetadataSql.CreateDentriesTable);
        conn.ExecuteNonQuery(SilkMetadataSql.CreateXAttrsTable);
        conn.ExecuteNonQuery(SilkMetadataSql.CreateWhiteoutsTable);
        conn.ExecuteNonQuery(SilkMetadataSql.DropInodeObjectsTable);
        conn.ExecuteNonQuery(SilkMetadataSql.DropObjectsTable);
        if (schemaVersion == null)
        {
            using var setSchema = conn.Prepare(SilkMetadataSql.UpsertSchemaVersion, persistent: false);
            setSchema.BindBlob(1, SchemaVersionUtf8);
            setSchema.ExecuteNonQuery();
        }

        var rootExists = conn.ExecuteScalarInt64(SilkMetadataSql.CountRootInode);
        if (rootExists == 0)
        {
            var now = GetUnixTimeNanoseconds();
            using var cmd = conn.Prepare(SilkMetadataSql.InsertRootInode, persistent: false);
            cmd.BindInt64(1, now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    internal SilkSqliteConnection OpenConnection()
    {
        return new SilkSqliteConnection(_dbPath);
    }

    private static int? TryReadSchemaVersion(SilkSqliteConnection conn)
    {
        if (!conn.TableExists("meta"))
            return null;

        using var stmt = conn.Prepare(SilkMetadataSql.GetSchemaVersion, persistent: false);
        if (!stmt.Step())
            return null;

        var rawVersion = stmt.ColumnBlobToArray(0);
        stmt.Reset();
        if (rawVersion.Length == 0)
            return null;
        if (!int.TryParse(System.Text.Encoding.ASCII.GetString(rawVersion), out var schemaVersion))
            throw new InvalidOperationException("SilkFS metadata schema version is invalid.");

        return schemaVersion;
    }

    private static void EnsureSqliteProviderInitialized()
    {
        if (Volatile.Read(ref _sqliteInit) == 2)
            return;

        if (Interlocked.CompareExchange(ref _sqliteInit, 1, 0) == 0)
        {
            try
            {
                raw.SetProvider(new SQLite3Provider_sqlite3());
                Volatile.Write(ref _sqliteInit, 2);
            }
            catch
            {
                Volatile.Write(ref _sqliteInit, 0);
                throw;
            }

            return;
        }

        var spinner = new SpinWait();
        while (Volatile.Read(ref _sqliteInit) != 2)
            spinner.SpinOnce();
    }

    internal static long GetUnixTimeNanoseconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
    }

    public sealed class SilkMetadataTransaction : IDisposable
    {
        private readonly SilkMetadataSession _session;
        private bool _disposed;

        internal SilkMetadataTransaction(SilkMetadataSession session)
        {
            _session = session;
        }

        public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
            long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
        {
            var now = SilkMetadataStore.GetUnixTimeNanoseconds();
            var effectiveAtime = atimeNs ?? now;
            var effectiveMtime = mtimeNs ?? now;
            var effectiveCtime = ctimeNs ?? now;
            var cmd = _session.GetUpsertInodeCommand();
            cmd.BindInt64(1, ino);
            cmd.BindInt32(2, (int)kind);
            cmd.BindInt32(3, mode);
            cmd.BindInt32(4, uid);
            cmd.BindInt32(5, gid);
            cmd.BindInt32(6, nlink);
            cmd.BindInt64(7, rdev);
            cmd.BindInt64(8, size);
            cmd.BindInt64(9, effectiveAtime);
            cmd.BindInt64(10, effectiveMtime);
            cmd.BindInt64(11, effectiveCtime);
            ExecuteNonQuery(cmd);
        }

        public void UpsertDentry(long parentIno, ReadOnlySpan<byte> name, long ino)
        {
            var cmd = _session.GetUpsertDentryCommand();
            cmd.BindInt64(1, parentIno);
            cmd.BindBlob(2, name);
            cmd.BindInt64(3, ino);
            ExecuteNonQuery(cmd);
        }

        public void RemoveDentry(long parentIno, ReadOnlySpan<byte> name)
        {
            var cmd = _session.GetRemoveDentryCommand();
            cmd.BindInt64(1, parentIno);
            cmd.BindBlob(2, name);
            ExecuteNonQuery(cmd);
        }

        public void DeleteInode(long ino)
        {
            var cmd = _session.GetDeleteInodeCommand();
            cmd.BindInt64(1, ino);
            ExecuteNonQuery(cmd);
        }

        public void MarkWhiteout(long parentIno, ReadOnlySpan<byte> name)
        {
            var cmd = _session.GetMarkWhiteoutCommand();
            cmd.BindInt64(1, parentIno);
            cmd.BindBlob(2, name);
            ExecuteNonQuery(cmd);
        }

        public void ClearWhiteout(long parentIno, ReadOnlySpan<byte> name)
        {
            var cmd = _session.GetClearWhiteoutCommand();
            cmd.BindInt64(1, parentIno);
            cmd.BindBlob(2, name);
            ExecuteNonQuery(cmd);
        }

        public void MarkOpaque(long parentIno)
        {
            var cmd = _session.GetMarkOpaqueCommand();
            cmd.BindInt64(1, parentIno);
            ExecuteNonQuery(cmd);
        }

        private static void ExecuteNonQuery(SilkSqliteStatement cmd)
        {
            cmd.ExecuteNonQuery();
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
    private readonly SilkSqliteConnection _conn;
    private SilkSqliteStatement? _clearOpaqueCmd;
    private SilkSqliteStatement? _clearWhiteoutCmd;
    private SilkSqliteStatement? _createInodeCmd;
    private SilkSqliteStatement? _deleteInodeCmd;
    private SilkSqliteStatement? _getInodeCmd;
    private SilkSqliteStatement? _getXAttrCmd;
    private SilkSqliteStatement? _hasWhiteoutCmd;
    private SilkSqliteStatement? _inodeExistsCmd;
    private SilkSqliteStatement? _isOpaqueCmd;
    private SilkSqliteStatement? _listDentriesByParentCmd;
    private SilkSqliteStatement? _listDentriesCmd;
    private SilkSqliteStatement? _listInodesCmd;
    private SilkSqliteStatement? _listOrphanInodesCmd;
    private SilkSqliteStatement? _listXAttrsCmd;
    private SilkSqliteStatement? _lookupDentryCmd;
    private SilkSqliteStatement? _markOpaqueCmd;
    private SilkSqliteStatement? _markWhiteoutCmd;
    private SilkSqliteStatement? _removeXAttrCmd;
    private SilkSqliteStatement? _removeDentryCmd;
    private SilkSqliteStatement? _setXAttrCmd;
    private SilkSqliteStatement? _upsertDentryCmd;
    private SilkSqliteStatement? _upsertInodeCmd;
    private bool _disposed;

    internal SilkMetadataSession(SilkMetadataStore store, SilkSqliteConnection connection)
    {
        Store = store;
        _conn = connection;
    }

    public SilkMetadataStore Store { get; }

    public long CreateInode(SilkInodeKind kind, int mode, int uid = 0, int gid = 0, uint rdev = 0)
    {
        var now = SilkMetadataStore.GetUnixTimeNanoseconds();
        _createInodeCmd ??= PrepareCommand(SilkMetadataSql.CreateInode);
        _createInodeCmd.BindInt32(1, (int)kind);
        _createInodeCmd.BindInt32(2, mode);
        _createInodeCmd.BindInt32(3, uid);
        _createInodeCmd.BindInt32(4, gid);
        _createInodeCmd.BindInt64(5, rdev);
        _createInodeCmd.BindInt64(6, now);
        _createInodeCmd.ExecuteNonQuery();
        return _conn.LastInsertRowId;
    }

    public void ExecuteTransaction(Action<SilkMetadataStore.SilkMetadataTransaction> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var tx = _conn.BeginTransaction();
        using var metadataTx = new SilkMetadataStore.SilkMetadataTransaction(this);
        action(metadataTx);
        tx.Commit();
    }

    public bool InodeExists(long ino)
    {
        _inodeExistsCmd ??= PrepareCommand(SilkMetadataSql.InodeExists);
        _inodeExistsCmd.BindInt64(1, ino);
        return _inodeExistsCmd.ExecuteScalarInt64() > 0;
    }

    public SilkInodeRecord? GetInode(long ino)
    {
        _getInodeCmd ??= PrepareCommand(SilkMetadataSql.GetInode);
        _getInodeCmd.BindInt64(1, ino);
        try
        {
            if (!_getInodeCmd.Step())
                return null;
            return ReadInode(_getInodeCmd);
        }
        finally
        {
            _getInodeCmd.Reset();
        }
    }

    public List<SilkInodeRecord> ListInodes()
    {
        _listInodesCmd ??= PrepareCommand(SilkMetadataSql.ListInodes);
        try
        {
            var result = new List<SilkInodeRecord>();
            while (_listInodesCmd.Step())
                result.Add(ReadInode(_listInodesCmd));
            return result;
        }
        finally
        {
            _listInodesCmd.Reset();
        }
    }

    public List<long> ListOrphanInodes()
    {
        _listOrphanInodesCmd ??= PrepareCommand(SilkMetadataSql.ListOrphanInodes);
        try
        {
            var result = new List<long>();
            while (_listOrphanInodesCmd.Step())
                result.Add(_listOrphanInodesCmd.ColumnInt64(0));
            return result;
        }
        finally
        {
            _listOrphanInodesCmd.Reset();
        }
    }

    public void UpsertInode(long ino, SilkInodeKind kind, int mode, int uid, int gid, int nlink = 1, uint rdev = 0,
        long size = 0, long? atimeNs = null, long? mtimeNs = null, long? ctimeNs = null)
    {
        ExecuteTransaction(tx => tx.UpsertInode(ino, kind, mode, uid, gid, nlink, rdev, size, atimeNs, mtimeNs, ctimeNs));
    }

    public void UpsertDentry(long parentIno, ReadOnlySpan<byte> name, long ino)
    {
        var ownedName = name.ToArray();
        ExecuteTransaction(tx => tx.UpsertDentry(parentIno, ownedName, ino));
    }

    public long? LookupDentry(long parentIno, ReadOnlySpan<byte> name)
    {
        _lookupDentryCmd ??= PrepareCommand(SilkMetadataSql.LookupDentry);
        _lookupDentryCmd.BindInt64(1, parentIno);
        _lookupDentryCmd.BindBlob(2, name);
        try
        {
            return _lookupDentryCmd.Step() ? _lookupDentryCmd.ColumnInt64(0) : null;
        }
        finally
        {
            _lookupDentryCmd.Reset();
        }
    }

    public List<SilkDentryRecord> ListDentries()
    {
        _listDentriesCmd ??= PrepareCommand(SilkMetadataSql.ListDentries);
        return ReadDentries(_listDentriesCmd);
    }

    public List<SilkDentryRecord> ListDentriesByParent(long parentIno)
    {
        _listDentriesByParentCmd ??= PrepareCommand(SilkMetadataSql.ListDentriesByParent);
        _listDentriesByParentCmd.BindInt64(1, parentIno);
        return ReadDentries(_listDentriesByParentCmd);
    }

    public void RemoveDentry(long parentIno, ReadOnlySpan<byte> name)
    {
        var ownedName = name.ToArray();
        ExecuteTransaction(tx => tx.RemoveDentry(parentIno, ownedName));
    }

    public void DeleteInode(long ino)
    {
        if (ino == SilkMetadataStore.RootInode)
            return;

        _deleteInodeCmd ??= PrepareCommand(SilkMetadataSql.DeleteInode);
        _deleteInodeCmd.BindInt64(1, ino);
        _deleteInodeCmd.ExecuteNonQuery();
    }

    public void SetXAttr(long ino, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _setXAttrCmd ??= PrepareCommand(SilkMetadataSql.SetXAttr);
        _setXAttrCmd.BindInt64(1, ino);
        _setXAttrCmd.BindBlob(2, key);
        _setXAttrCmd.BindBlob(3, value);
        _setXAttrCmd.ExecuteNonQuery();
    }

    public byte[]? GetXAttr(long ino, ReadOnlySpan<byte> key)
    {
        _getXAttrCmd ??= PrepareCommand(SilkMetadataSql.GetXAttr);
        _getXAttrCmd.BindInt64(1, ino);
        _getXAttrCmd.BindBlob(2, key);
        try
        {
            return _getXAttrCmd.Step() ? _getXAttrCmd.ColumnBlobToArray(0) : null;
        }
        finally
        {
            _getXAttrCmd.Reset();
        }
    }

    public List<SilkXAttrRecord> ListXAttrs(long ino)
    {
        _listXAttrsCmd ??= PrepareCommand(SilkMetadataSql.ListXAttrs);
        _listXAttrsCmd.BindInt64(1, ino);
        try
        {
            var result = new List<SilkXAttrRecord>();
            while (_listXAttrsCmd.Step())
                result.Add(new SilkXAttrRecord(_listXAttrsCmd.ColumnBlobToArray(0), _listXAttrsCmd.ColumnBlobToArray(1)));
            return result;
        }
        finally
        {
            _listXAttrsCmd.Reset();
        }
    }

    public void RemoveXAttr(long ino, ReadOnlySpan<byte> key)
    {
        _removeXAttrCmd ??= PrepareCommand(SilkMetadataSql.RemoveXAttr);
        _removeXAttrCmd.BindInt64(1, ino);
        _removeXAttrCmd.BindBlob(2, key);
        _removeXAttrCmd.ExecuteNonQuery();
    }

    public void MarkWhiteout(long parentIno, ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            throw new ArgumentException("Whiteout name cannot be empty.", nameof(name));
        var ownedName = name.ToArray();
        ExecuteTransaction(tx => tx.MarkWhiteout(parentIno, ownedName));
    }

    public bool HasWhiteout(long parentIno, ReadOnlySpan<byte> name)
    {
        _hasWhiteoutCmd ??= PrepareCommand(SilkMetadataSql.HasWhiteout);
        _hasWhiteoutCmd.BindInt64(1, parentIno);
        _hasWhiteoutCmd.BindBlob(2, name);
        return _hasWhiteoutCmd.ExecuteScalarInt64() > 0;
    }

    public void ClearWhiteout(long parentIno, ReadOnlySpan<byte> name)
    {
        var ownedName = name.ToArray();
        ExecuteTransaction(tx => tx.ClearWhiteout(parentIno, ownedName));
    }

    public void MarkOpaque(long parentIno)
    {
        ExecuteTransaction(tx => tx.MarkOpaque(parentIno));
    }

    public bool IsOpaque(long parentIno)
    {
        _isOpaqueCmd ??= PrepareCommand(SilkMetadataSql.IsOpaque);
        _isOpaqueCmd.BindInt64(1, parentIno);
        return _isOpaqueCmd.ExecuteScalarInt64() > 0;
    }

    public void ClearOpaque(long parentIno)
    {
        _clearOpaqueCmd ??= PrepareCommand(SilkMetadataSql.ClearOpaque);
        _clearOpaqueCmd.BindInt64(1, parentIno);
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

    private SilkSqliteStatement PrepareCommand(ReadOnlySpan<byte> sql)
    {
        return _conn.Prepare(sql);
    }

    internal SilkSqliteStatement GetClearWhiteoutCommand()
    {
        return _clearWhiteoutCmd ??= PrepareCommand(SilkMetadataSql.ClearWhiteout);
    }

    internal SilkSqliteStatement GetMarkOpaqueCommand()
    {
        return _markOpaqueCmd ??= PrepareCommand(SilkMetadataSql.MarkOpaque);
    }

    internal SilkSqliteStatement GetMarkWhiteoutCommand()
    {
        return _markWhiteoutCmd ??= PrepareCommand(SilkMetadataSql.MarkWhiteout);
    }

    internal SilkSqliteStatement GetRemoveDentryCommand()
    {
        return _removeDentryCmd ??= PrepareCommand(SilkMetadataSql.RemoveDentry);
    }

    internal SilkSqliteStatement GetDeleteInodeCommand()
    {
        return _deleteInodeCmd ??= PrepareCommand(SilkMetadataSql.DeleteInode);
    }

    internal SilkSqliteStatement GetUpsertDentryCommand()
    {
        return _upsertDentryCmd ??= PrepareCommand(SilkMetadataSql.UpsertDentry);
    }

    internal SilkSqliteStatement GetUpsertInodeCommand()
    {
        return _upsertInodeCmd ??= PrepareCommand(SilkMetadataSql.UpsertInode);
    }

    private static void DisposeCommand(ref SilkSqliteStatement? cmd)
    {
        cmd?.Dispose();
        cmd = null;
    }

    private static SilkInodeRecord ReadInode(SilkSqliteStatement stmt)
    {
        return new SilkInodeRecord(
            stmt.ColumnInt64(0),
            (SilkInodeKind)stmt.ColumnInt32(1),
            stmt.ColumnInt32(2),
            stmt.ColumnInt32(3),
            stmt.ColumnInt32(4),
            stmt.ColumnInt64(5),
            stmt.ColumnInt64(6),
            stmt.ColumnInt64(7),
            stmt.ColumnInt64(8),
            stmt.ColumnInt64(9),
            stmt.ColumnInt64(10));
    }

    private static List<SilkDentryRecord> ReadDentries(SilkSqliteStatement stmt)
    {
        try
        {
            var result = new List<SilkDentryRecord>();
            while (stmt.Step())
                result.Add(new SilkDentryRecord(stmt.ColumnInt64(0), stmt.ColumnBlobToArray(1), stmt.ColumnInt64(2)));
            return result;
        }
        finally
        {
            stmt.Reset();
        }
    }
}

internal static class SilkMetadataSql
{
    internal static ReadOnlySpan<byte> PragmaJournalModeWal => "PRAGMA journal_mode=WAL"u8;

    internal static ReadOnlySpan<byte> CreateMetaTable => """
                                                           CREATE TABLE IF NOT EXISTS meta (
                                                             k TEXT PRIMARY KEY,
                                                             v TEXT NOT NULL
                                                           )
                                                           """u8;

    internal static ReadOnlySpan<byte> CreateInodesTable => """
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
                                                             )
                                                             """u8;

    internal static ReadOnlySpan<byte> CreateDentriesTable => """
                                                               CREATE TABLE IF NOT EXISTS dentries (
                                                                 parent_ino INTEGER NOT NULL,
                                                                 name BLOB NOT NULL,
                                                                 ino INTEGER NOT NULL,
                                                                 PRIMARY KEY (parent_ino, name),
                                                                 FOREIGN KEY (ino) REFERENCES inodes(ino) ON DELETE CASCADE
                                                               )
                                                               """u8;

    internal static ReadOnlySpan<byte> CreateXAttrsTable => """
                                                             CREATE TABLE IF NOT EXISTS xattrs (
                                                               ino INTEGER NOT NULL,
                                                               key BLOB NOT NULL,
                                                               value BLOB NOT NULL,
                                                               PRIMARY KEY (ino, key),
                                                               FOREIGN KEY (ino) REFERENCES inodes(ino) ON DELETE CASCADE
                                                             )
                                                             """u8;

    internal static ReadOnlySpan<byte> CreateWhiteoutsTable => """
                                                                CREATE TABLE IF NOT EXISTS whiteouts (
                                                                  parent_ino INTEGER NOT NULL,
                                                                  name BLOB NOT NULL,
                                                                  opaque INTEGER NOT NULL DEFAULT 0,
                                                                  PRIMARY KEY (parent_ino, name)
                                                                )
                                                                """u8;

    internal static ReadOnlySpan<byte> DropInodeObjectsTable => "DROP TABLE IF EXISTS inode_objects"u8;
    internal static ReadOnlySpan<byte> DropObjectsTable => "DROP TABLE IF EXISTS objects"u8;

    internal static ReadOnlySpan<byte> GetSchemaVersion => "SELECT CAST(v AS BLOB) FROM meta WHERE k = 'schema_version'"u8;

    internal static ReadOnlySpan<byte> UpsertSchemaVersion => """
                                                               INSERT INTO meta(k, v) VALUES ('schema_version', CAST(?1 AS TEXT))
                                                               ON CONFLICT(k) DO UPDATE SET v = excluded.v
                                                               """u8;

    internal static ReadOnlySpan<byte> CountRootInode => "SELECT COUNT(1) FROM inodes WHERE ino = 1"u8;

    internal static ReadOnlySpan<byte> InsertRootInode => """
                                                           INSERT INTO inodes(ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns)
                                                           VALUES (1, 2, 511, 0, 0, 2, 0, 0, ?1, ?1, ?1)
                                                           """u8;

    internal static ReadOnlySpan<byte> CreateInode => """
                                                       INSERT INTO inodes(kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns)
                                                       VALUES (?1, ?2, ?3, ?4, 1, ?5, 0, ?6, ?6, ?6)
                                                       """u8;

    internal static ReadOnlySpan<byte> InodeExists => "SELECT COUNT(1) FROM inodes WHERE ino = ?1"u8;

    internal static ReadOnlySpan<byte> GetInode => """
                                                    SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns
                                                    FROM inodes
                                                    WHERE ino = ?1
                                                    """u8;

    internal static ReadOnlySpan<byte> ListInodes => """
                                                      SELECT ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns
                                                      FROM inodes
                                                      ORDER BY ino ASC
                                                      """u8;

    internal static ReadOnlySpan<byte> ListOrphanInodes => "SELECT ino FROM inodes WHERE ino <> 1 AND nlink <= 0 ORDER BY ino ASC"u8;

    internal static ReadOnlySpan<byte> LookupDentry => "SELECT ino FROM dentries WHERE parent_ino = ?1 AND name = ?2"u8;

    internal static ReadOnlySpan<byte> ListDentries => "SELECT parent_ino, name, ino FROM dentries ORDER BY parent_ino ASC, name ASC"u8;

    internal static ReadOnlySpan<byte> ListDentriesByParent => "SELECT parent_ino, name, ino FROM dentries WHERE parent_ino = ?1 ORDER BY name ASC"u8;

    internal static ReadOnlySpan<byte> DeleteInode => "DELETE FROM inodes WHERE ino = ?1"u8;

    internal static ReadOnlySpan<byte> SetXAttr => """
                                                    INSERT INTO xattrs(ino, key, value) VALUES (?1, ?2, ?3)
                                                    ON CONFLICT(ino, key) DO UPDATE SET value = excluded.value
                                                    """u8;

    internal static ReadOnlySpan<byte> GetXAttr => "SELECT value FROM xattrs WHERE ino = ?1 AND key = ?2"u8;

    internal static ReadOnlySpan<byte> ListXAttrs => "SELECT key, value FROM xattrs WHERE ino = ?1 ORDER BY key ASC"u8;

    internal static ReadOnlySpan<byte> RemoveXAttr => "DELETE FROM xattrs WHERE ino = ?1 AND key = ?2"u8;

    internal static ReadOnlySpan<byte> HasWhiteout => "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = ?1 AND name = ?2 AND opaque = 0"u8;

    internal static ReadOnlySpan<byte> IsOpaque => "SELECT COUNT(1) FROM whiteouts WHERE parent_ino = ?1 AND name = X'2E77682E2E77682E2E6F7071' AND opaque = 1"u8;

    internal static ReadOnlySpan<byte> ClearOpaque => "DELETE FROM whiteouts WHERE parent_ino = ?1 AND name = X'2E77682E2E77682E2E6F7071' AND opaque = 1"u8;

    internal static ReadOnlySpan<byte> ClearWhiteout => "DELETE FROM whiteouts WHERE parent_ino = ?1 AND name = ?2"u8;

    internal static ReadOnlySpan<byte> MarkOpaque => """
                                                      INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (?1, X'2E77682E2E77682E2E6F7071', 1)
                                                      ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 1
                                                      """u8;

    internal static ReadOnlySpan<byte> MarkWhiteout => """
                                                        INSERT INTO whiteouts(parent_ino, name, opaque) VALUES (?1, ?2, 0)
                                                        ON CONFLICT(parent_ino, name) DO UPDATE SET opaque = 0
                                                        """u8;

    internal static ReadOnlySpan<byte> RemoveDentry => "DELETE FROM dentries WHERE parent_ino = ?1 AND name = ?2"u8;

    internal static ReadOnlySpan<byte> UpsertDentry => """
                                                        INSERT INTO dentries(parent_ino, name, ino) VALUES (?1, ?2, ?3)
                                                        ON CONFLICT(parent_ino, name) DO UPDATE SET ino = excluded.ino
                                                        """u8;

    internal static ReadOnlySpan<byte> UpsertInode => """
                                                       INSERT INTO inodes(ino, kind, mode, uid, gid, nlink, rdev, size, atime_ns, mtime_ns, ctime_ns)
                                                       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
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
                                                         ctime_ns = excluded.ctime_ns
                                                       """u8;
}
