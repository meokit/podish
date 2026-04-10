using System.Buffers;
using System.Text;
using SQLitePCL;

namespace Fiberish.SilkFS;

internal sealed class SilkSqliteConnection : IDisposable
{
    private sqlite3? _db;
    private bool _disposed;

    public SilkSqliteConnection(string path)
    {
        var rc = raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, vfs: null);
        if (rc != raw.SQLITE_OK)
        {
            var message = db != null && !db.IsInvalid
                ? raw.sqlite3_errmsg(db).utf8_to_string()
                : $"SQLite error {rc}";
            if (db != null && !db.IsInvalid)
                raw.sqlite3_close_v2(db);
            throw new InvalidOperationException($"Failed to open SilkFS metadata db '{path}': {message} (rc={rc}).");
        }

        _db = db;
        ThrowIfError(raw.sqlite3_extended_result_codes(db, 1));
        using var foreignKeys = Prepare("PRAGMA foreign_keys=ON"u8, persistent: false);
        foreignKeys.ExecuteNonQuery();
    }

    internal sqlite3 Handle => _db ?? throw new ObjectDisposedException(nameof(SilkSqliteConnection));

    public long LastInsertRowId => raw.sqlite3_last_insert_rowid(Handle);

    public SilkSqliteStatement Prepare(ReadOnlySpan<byte> sql, bool persistent = true)
    {
        var flags = persistent ? (uint)raw.SQLITE_PREPARE_PERSISTENT : 0u;
        var rc = raw.sqlite3_prepare_v3(Handle, sql, flags, out var stmt, out var tail);
        if (rc != raw.SQLITE_OK)
            ThrowIfError(rc);
        if (stmt == null || stmt.IsInvalid)
            throw new InvalidOperationException("SQLite prepared statement was unexpectedly null.");
        if (!IsIgnorableTail(tail))
        {
            raw.sqlite3_finalize(stmt);
            throw new InvalidOperationException("SQLite SQL contains unexpected trailing text.");
        }

        return new SilkSqliteStatement(this, stmt);
    }

    public void ExecuteNonQuery(ReadOnlySpan<byte> sql)
    {
        using var stmt = Prepare(sql, persistent: false);
        stmt.ExecuteNonQuery();
    }

    public long ExecuteScalarInt64(ReadOnlySpan<byte> sql)
    {
        using var stmt = Prepare(sql, persistent: false);
        return stmt.ExecuteScalarInt64();
    }

    public SilkSqliteTransaction BeginTransaction()
    {
        return new SilkSqliteTransaction(this);
    }

    public bool TableExists(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        using var stmt = Prepare("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?1"u8,
            persistent: false);
        stmt.BindText(1, tableName);
        return stmt.ExecuteScalarInt64() > 0;
    }

    internal void ThrowIfError(int rc, sqlite3_stmt? stmt = null)
    {
        if (rc == raw.SQLITE_OK || rc == raw.SQLITE_ROW || rc == raw.SQLITE_DONE)
            return;

        throw CreateException(rc, stmt);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var db = _db;
        _db = null;
        _disposed = true;
        if (db != null && !db.IsInvalid)
            raw.sqlite3_close_v2(db);
    }

    private Exception CreateException(int rc, sqlite3_stmt? stmt)
    {
        var message = raw.sqlite3_errmsg(Handle).utf8_to_string();
        var extended = raw.sqlite3_extended_errcode(Handle);
        if (stmt == null)
            return new InvalidOperationException($"SQLite failure rc={rc}, extended={extended}: {message}");

        var sql = raw.sqlite3_sql(stmt).utf8_to_string();
        return new InvalidOperationException($"SQLite failure rc={rc}, extended={extended}: {message}. SQL: {sql}");
    }

    private static bool IsIgnorableTail(ReadOnlySpan<byte> tail)
    {
        foreach (var b in tail)
        {
            if (b is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
                return false;
        }

        return true;
    }
}

internal sealed class SilkSqliteTransaction : IDisposable
{
    private readonly SilkSqliteConnection _connection;
    private bool _completed;

    public SilkSqliteTransaction(SilkSqliteConnection connection)
    {
        _connection = connection;
        _connection.ExecuteNonQuery("BEGIN"u8);
    }

    public void Commit()
    {
        if (_completed)
            return;

        _connection.ExecuteNonQuery("COMMIT"u8);
        _completed = true;
    }

    public void Dispose()
    {
        if (_completed)
            return;

        try
        {
            _connection.ExecuteNonQuery("ROLLBACK"u8);
        }
        catch
        {
        }

        _completed = true;
    }
}

internal sealed class SilkSqliteStatement : IDisposable
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private readonly SilkSqliteConnection _connection;
    private readonly sqlite3_stmt _stmt;
    private readonly byte[]?[] _parameterBuffers;
    private bool _disposed;

    public SilkSqliteStatement(SilkSqliteConnection connection, sqlite3_stmt stmt)
    {
        _connection = connection;
        _stmt = stmt;
        var parameterCount = raw.sqlite3_bind_parameter_count(stmt);
        _parameterBuffers = parameterCount == 0 ? Array.Empty<byte[]?>() : new byte[]?[parameterCount + 1];
    }

    public void BindInt32(int index, int value)
    {
        _connection.ThrowIfError(raw.sqlite3_bind_int(_stmt, index, value), _stmt);
    }

    public void BindInt64(int index, long value)
    {
        _connection.ThrowIfError(raw.sqlite3_bind_int64(_stmt, index, value), _stmt);
    }

    public void BindText(int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            _connection.ThrowIfError(raw.sqlite3_bind_text(_stmt, index, ReadOnlySpan<byte>.Empty), _stmt);
            return;
        }

        var byteCount = Utf8.GetByteCount(value);
        var buffer = RentParameterBuffer(index, byteCount);
        var written = Utf8.GetBytes(value.AsSpan(), buffer);
        _connection.ThrowIfError(raw.sqlite3_bind_text(_stmt, index, buffer[..written]), _stmt);
    }

    public void BindTextUtf8(int index, ReadOnlySpan<byte> value)
    {
        var buffer = CopyToParameterBuffer(index, value);
        _connection.ThrowIfError(raw.sqlite3_bind_text(_stmt, index, buffer), _stmt);
    }

    public void BindBlob(int index, ReadOnlySpan<byte> value)
    {
        var buffer = CopyToParameterBuffer(index, value);
        _connection.ThrowIfError(raw.sqlite3_bind_blob(_stmt, index, buffer), _stmt);
    }

    public void ExecuteNonQuery()
    {
        try
        {
            while (true)
            {
                var rc = raw.sqlite3_step(_stmt);
                if (rc == raw.SQLITE_DONE)
                    return;
                if (rc != raw.SQLITE_ROW)
                    _connection.ThrowIfError(rc, _stmt);
            }
        }
        finally
        {
            Reset();
        }
    }

    public long ExecuteScalarInt64()
    {
        try
        {
            var rc = raw.sqlite3_step(_stmt);
            if (rc != raw.SQLITE_ROW)
            {
                if (rc == raw.SQLITE_DONE)
                    throw new InvalidOperationException("SQLite scalar query returned no rows.");
                _connection.ThrowIfError(rc, _stmt);
            }

            return raw.sqlite3_column_int64(_stmt, 0);
        }
        finally
        {
            Reset();
        }
    }

    public bool Step()
    {
        var rc = raw.sqlite3_step(_stmt);
        if (rc == raw.SQLITE_ROW)
            return true;
        if (rc == raw.SQLITE_DONE)
            return false;

        _connection.ThrowIfError(rc, _stmt);
        return false;
    }

    public int ColumnInt32(int index)
    {
        return raw.sqlite3_column_int(_stmt, index);
    }

    public long ColumnInt64(int index)
    {
        return raw.sqlite3_column_int64(_stmt, index);
    }

    public string ColumnString(int index)
    {
        return raw.sqlite3_column_text(_stmt, index).utf8_to_string();
    }

    public ReadOnlySpan<byte> ColumnBlob(int index)
    {
        return raw.sqlite3_column_blob(_stmt, index);
    }

    public byte[] ColumnBlobToArray(int index)
    {
        return ColumnBlob(index).ToArray();
    }

    public void Reset()
    {
        if (_disposed)
            return;

        raw.sqlite3_reset(_stmt);
        raw.sqlite3_clear_bindings(_stmt);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var buffer in _parameterBuffers)
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        raw.sqlite3_finalize(_stmt);
        _disposed = true;
    }

    private Span<byte> RentParameterBuffer(int index, int minLength)
    {
        if (minLength == 0)
            return Span<byte>.Empty;

        var buffer = _parameterBuffers[index];
        if (buffer == null || buffer.Length < minLength)
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            buffer = ArrayPool<byte>.Shared.Rent(minLength);
            _parameterBuffers[index] = buffer;
        }

        return buffer.AsSpan(0, minLength);
    }

    private ReadOnlySpan<byte> CopyToParameterBuffer(int index, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return ReadOnlySpan<byte>.Empty;

        var buffer = RentParameterBuffer(index, value.Length);
        value.CopyTo(buffer);
        return buffer[..value.Length];
    }
}
