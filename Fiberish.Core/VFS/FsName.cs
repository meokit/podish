using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Fiberish.VFS;

public readonly struct FsName : IEquatable<FsName>, IComparable<FsName>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static readonly ConcurrentDictionary<string, FsName> GeneratedLiteralByString = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<int, FsName[]> GeneratedLiteralByHash = new();

    private readonly byte[]? _bytes;
    private readonly int _hashCode;

    private FsName(byte[] ownedBytes, int hashCode)
    {
        _bytes = ownedBytes;
        _hashCode = hashCode;
    }

    public static FsName Empty => default;

    public static IComparer<FsName> BytewiseComparer { get; } = new BytewiseFsNameComparer();

    public ReadOnlySpan<byte> Bytes => _bytes ?? Array.Empty<byte>();

    public byte this[int index] => Bytes[index];

    public string this[Range range] => ToString()[range];

    public int Length => _bytes?.Length ?? 0;

    public bool IsEmpty => Length == 0;

    public bool IsDot => _bytes is { Length: 1 } bytes && bytes[0] == (byte)'.';

    public bool IsDotDot => _bytes is { Length: 2 } bytes && bytes[0] == (byte)'.' && bytes[1] == (byte)'.';

    public bool IsDotOrDotDot => IsDot || IsDotDot;

    public static FsName FromOwnedBytes(byte[] ownedBytes, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(ownedBytes);
        ValidateComponentBytes(ownedBytes, allowEmpty);
        return new FsName(ownedBytes, ComputeHash(ownedBytes));
    }

    public static FsName FromBytes(ReadOnlySpan<byte> value, bool allowEmpty = false)
    {
        if (TryGetGeneratedLiteral(value, out var generated))
            return generated;
        ValidateComponentBytes(value, allowEmpty);
        if (value.IsEmpty)
            return Empty;
        return new FsName(value.ToArray(), ComputeHash(value));
    }

    public static FsName FromString(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (TryGetGeneratedLiteral(value, out var generated))
            return generated;
        return FromOwnedBytes(Utf8.GetBytes(value), allowEmpty);
    }

    public static FsName FromConstructorString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 || value == "/" ? Empty : FromString(value);
    }

    public bool Equals(FsName other)
    {
        return Bytes.SequenceEqual(other.Bytes);
    }

    public bool Equals(ReadOnlySpan<byte> other)
    {
        return Bytes.SequenceEqual(other);
    }

    public int CompareTo(FsName other)
    {
        return CompareBytes(Bytes, other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is FsName other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }

    public override string ToString()
    {
        return FsEncoding.DecodeUtf8Lossy(Bytes);
    }

    public string ToDebugString()
    {
        if (FsEncoding.TryDecodeUtf8(Bytes, out var decoded))
            return decoded;

        if (Bytes.IsEmpty)
            return string.Empty;

        return string.Create(Bytes.Length * 4, this, static (dst, name) =>
        {
            var offset = 0;
            foreach (var b in name.Bytes)
            {
                dst[offset++] = '\\';
                dst[offset++] = 'x';
                dst[offset++] = GetHexNibble((b >> 4) & 0x0F);
                dst[offset++] = GetHexNibble(b & 0x0F);
            }
        });
    }

    public static int ComputeHash(ReadOnlySpan<byte> value)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var b in value)
                hash = (hash ^ b) * prime;
            return (int)hash;
        }
    }

    internal static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var count = Math.Min(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0)
                return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }

    public static void RegisterGeneratedLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
            return;

        var ownedBytes = Utf8.GetBytes(value);
        ValidateComponentBytes(ownedBytes, false);

        var literal = new FsName(ownedBytes, ComputeHash(ownedBytes));
        if (!GeneratedLiteralByString.TryAdd(value, literal))
            return;

        GeneratedLiteralByHash.AddOrUpdate(
            literal._hashCode,
            new FsName[] { literal },
            (_, existing) =>
            {
                foreach (var candidate in existing)
                {
                    if (candidate.Equals(literal))
                        return existing;
                }

                var next = new FsName[existing.Length + 1];
                Array.Copy(existing, next, existing.Length);
                next[^1] = literal;
                return next;
            });
    }

    private static bool TryGetGeneratedLiteral(string value, out FsName literal)
    {
        if (value.Length == 0)
        {
            literal = default;
            return false;
        }

        return GeneratedLiteralByString.TryGetValue(value, out literal);
    }

    private static bool TryGetGeneratedLiteral(ReadOnlySpan<byte> value, out FsName literal)
    {
        if (value.IsEmpty)
        {
            literal = default;
            return false;
        }

        if (!GeneratedLiteralByHash.TryGetValue(ComputeHash(value), out var candidates))
        {
            literal = default;
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(value))
            {
                literal = candidate;
                return true;
            }
        }

        literal = default;
        return false;
    }

    private static void ValidateComponentBytes(ReadOnlySpan<byte> value, bool allowEmpty)
    {
        if (!allowEmpty && value.IsEmpty)
            throw new ArgumentException("Filesystem component cannot be empty.", nameof(value));

        foreach (var b in value)
        {
            if (b == 0)
                throw new ArgumentException("Filesystem component cannot contain NUL.", nameof(value));
            if (b == (byte)'/')
                throw new ArgumentException("Filesystem component cannot contain '/'.", nameof(value));
        }
    }

    private static char GetHexNibble(int value)
    {
        return (char)(value < 10 ? '0' + value : 'A' + (value - 10));
    }

    public static bool operator ==(FsName left, FsName right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(FsName left, FsName right)
    {
        return !left.Equals(right);
    }

    public static bool operator ==(FsName left, string right)
    {
        return left.EqualsString(right);
    }

    public static bool operator !=(FsName left, string right)
    {
        return !left.EqualsString(right);
    }

    public static bool operator ==(string left, FsName right)
    {
        return right.EqualsString(left);
    }

    public static bool operator !=(string left, FsName right)
    {
        return !right.EqualsString(left);
    }

    public static implicit operator FsName(string value)
    {
        return FromConstructorString(value);
    }

    public static implicit operator string(FsName value)
    {
        return value.ToString();
    }

    private bool EqualsString(string? value)
    {
        if (value == null)
            return false;

        var bytes = Bytes;
        if (value.Length == bytes.Length)
        {
            var asciiMatch = true;
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch > 0x7F || bytes[i] != (byte)ch)
                {
                    asciiMatch = false;
                    break;
                }
            }

            if (asciiMatch)
                return true;
        }

        var encodedLength = Utf8.GetByteCount(value);
        if (encodedLength != bytes.Length)
            return false;

        byte[]? rented = null;
        try
        {
            Span<byte> encoded = encodedLength <= 256
                ? stackalloc byte[encodedLength]
                : (rented = ArrayPool<byte>.Shared.Rent(encodedLength)).AsSpan(0, encodedLength);
            var written = Utf8.GetBytes(value, encoded);
            return bytes.SequenceEqual(encoded[..written]);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private sealed class BytewiseFsNameComparer : IComparer<FsName>
    {
        public int Compare(FsName x, FsName y)
        {
            return CompareBytes(x.Bytes, y.Bytes);
        }
    }
}

public static class FsEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static byte[] EncodeUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Utf8.GetBytes(value);
    }

    public static byte[] EncodeUtf8Strict(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StrictUtf8.GetBytes(value);
    }

    public static bool TryEncodeUtf8(string value, out byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            encoded = StrictUtf8.GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            encoded = [];
            return false;
        }
    }

    public static bool TryDecodeUtf8(ReadOnlySpan<byte> value, out string decoded)
    {
        try
        {
            decoded = StrictUtf8.GetString(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> value)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static bool TryParseAsciiInt32(ReadOnlySpan<byte> value, out int parsed)
    {
        if (value.IsEmpty)
        {
            parsed = 0;
            return false;
        }

        var acc = 0;
        foreach (var b in value)
        {
            var digit = b - (byte)'0';
            if (digit > 9)
            {
                parsed = 0;
                return false;
            }

            if (acc > (int.MaxValue - digit) / 10)
            {
                parsed = 0;
                return false;
            }

            acc = (acc * 10) + digit;
        }

        parsed = acc;
        return true;
    }

    public static string DecodeUtf8Strict(ReadOnlySpan<byte> value)
    {
        return StrictUtf8.GetString(value);
    }

    public static string DecodeUtf8Lossy(ReadOnlySpan<byte> value)
    {
        return Utf8.GetString(value);
    }
}

public sealed class FsNameMap<T> : IEnumerable<KeyValuePair<FsName, T>>
{
    private readonly Dictionary<int, List<Entry>> _buckets = [];
    private int _count;

    public int Count => _count;

    public IEnumerable<T> Values => this.Select(static pair => pair.Value);

    public void Clear()
    {
        _buckets.Clear();
        _count = 0;
    }

    public void Set(FsName key, T value)
    {
        var hash = key.GetHashCode();
        if (!_buckets.TryGetValue(hash, out var bucket))
        {
            _buckets[hash] = [new Entry(key, value)];
            _count++;
            return;
        }

        for (var i = 0; i < bucket.Count; i++)
            if (bucket[i].Key == key)
            {
                bucket[i] = new Entry(key, value);
                return;
            }

        bucket.Add(new Entry(key, value));
        _count++;
    }

    public bool TryGetValue(FsName key, out T value)
    {
        return TryGetValue(key.Bytes, out value);
    }

    public bool TryGetValue(ReadOnlySpan<byte> key, out T value)
    {
        if (_buckets.TryGetValue(FsName.ComputeHash(key), out var bucket))
            foreach (var entry in CollectionsMarshal.AsSpan(bucket))
                if (entry.Key.Equals(key))
                {
                    value = entry.Value;
                    return true;
                }

        value = default!;
        return false;
    }

    public bool Remove(FsName key, out T? removed)
    {
        return Remove(key.Bytes, out removed);
    }

    public bool Remove(FsName key)
    {
        return Remove(key.Bytes, out _);
    }

    public bool Remove(ReadOnlySpan<byte> key, out T? removed)
    {
        if (_buckets.TryGetValue(FsName.ComputeHash(key), out var bucket))
            for (var i = 0; i < bucket.Count; i++)
                if (bucket[i].Key.Equals(key))
                {
                    removed = bucket[i].Value;
                    bucket.RemoveAt(i);
                    if (bucket.Count == 0)
                        _buckets.Remove(FsName.ComputeHash(key));
                    _count--;
                    return true;
                }

        removed = default;
        return false;
    }

    public bool Remove(ReadOnlySpan<byte> key)
    {
        return Remove(key, out _);
    }

    public IEnumerator<KeyValuePair<FsName, T>> GetEnumerator()
    {
        foreach (var bucket in _buckets.Values)
            foreach (var entry in bucket)
                yield return new KeyValuePair<FsName, T>(entry.Key, entry.Value);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private readonly record struct Entry(FsName Key, T Value);
}

public sealed class FsNameSet : IEnumerable<FsName>
{
    private readonly Dictionary<int, List<FsName>> _buckets = [];
    private int _count;

    public int Count => _count;

    public void Clear()
    {
        _buckets.Clear();
        _count = 0;
    }

    public bool Add(FsName name)
    {
        var hash = name.GetHashCode();
        if (!_buckets.TryGetValue(hash, out var bucket))
        {
            _buckets[hash] = [name];
            _count++;
            return true;
        }

        foreach (var existing in CollectionsMarshal.AsSpan(bucket))
            if (existing == name)
                return false;

        bucket.Add(name);
        _count++;
        return true;
    }

    public bool Contains(FsName name)
    {
        return Contains(name.Bytes);
    }

    public bool Contains(ReadOnlySpan<byte> name)
    {
        if (_buckets.TryGetValue(FsName.ComputeHash(name), out var bucket))
            foreach (var existing in CollectionsMarshal.AsSpan(bucket))
                if (existing.Equals(name))
                    return true;

        return false;
    }

    public bool Remove(FsName name)
    {
        return Remove(name.Bytes);
    }

    public bool Remove(ReadOnlySpan<byte> name)
    {
        if (_buckets.TryGetValue(FsName.ComputeHash(name), out var bucket))
            for (var i = 0; i < bucket.Count; i++)
                if (bucket[i].Equals(name))
                {
                    bucket.RemoveAt(i);
                    if (bucket.Count == 0)
                        _buckets.Remove(FsName.ComputeHash(name));
                    _count--;
                    return true;
                }

        return false;
    }

    public IEnumerator<FsName> GetEnumerator()
    {
        foreach (var bucket in _buckets.Values)
            foreach (var name in bucket)
                yield return name;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
