using System.Text;
using Fiberish.VFS;

namespace Podish.Core;

public static class OciLayerIndexBuilder
{
    public static LayerIndex BuildFromTar(Stream tarStream, string blobDigest)
    {
        if (!tarStream.CanRead) throw new ArgumentException("Tar stream must be readable", nameof(tarStream));

        var index = new LayerIndex();
        var header = new byte[512];

        while (true)
        {
            ReadExactly(tarStream, header);
            if (IsZeroBlock(header)) break;

            var name = ReadString(header.AsSpan(0, 100));
            var mode = ParseOctal(header.AsSpan(100, 8), 0x1A4);
            var uid = ParseOctal(header.AsSpan(108, 8), 0);
            var gid = ParseOctal(header.AsSpan(116, 8), 0);
            var size = (ulong)ParseOctalLong(header.AsSpan(124, 12), 0);
            var mtimeUnix = ParseOctalLong(header.AsSpan(136, 12), 0);
            var typeFlag = header[156];
            var linkName = ReadString(header.AsSpan(157, 100));
            var prefix = ReadString(header.AsSpan(345, 155));

            if (!string.IsNullOrEmpty(prefix))
                name = $"{prefix}/{name}";
            name = NormalizeEntryPath(name);
            var path = "/" + name;
            var mtime = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, mtimeUnix)).UtcDateTime;

            var dataOffset = tarStream.CanSeek ? tarStream.Position : -1;

            switch (typeFlag)
            {
                case (byte)'5': // directory
                    if (path.EndsWith('/')) path = path[..^1];
                    index.AddEntry(new LayerIndexEntry(path, InodeType.Directory, mode, uid, gid, 0, "",
                        mtime, mtime, mtime, null, -1, blobDigest));
                    break;
                case (byte)'2': // symlink
                    index.AddEntry(new LayerIndexEntry(path, InodeType.Symlink, mode, uid, gid, 0, linkName,
                        mtime, mtime, mtime, null, -1, blobDigest));
                    break;
                case (byte)'0':
                case 0: // regular file
                    index.AddEntry(new LayerIndexEntry(path, InodeType.File, mode, uid, gid, size, "",
                        mtime, mtime, mtime, null, dataOffset, blobDigest));
                    break;
                default:
                    break;
            }

            SkipAligned(tarStream, size);
        }

        return index;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n <= 0) throw new EndOfStreamException("Unexpected EOF while reading tar");
            total += n;
        }
    }

    private static bool IsZeroBlock(ReadOnlySpan<byte> block)
    {
        foreach (var b in block)
            if (b != 0)
                return false;
        return true;
    }

    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.UTF8.GetString(bytes[..end]).Trim();
    }

    private static int ParseOctal(ReadOnlySpan<byte> bytes, int fallback)
    {
        var s = ReadString(bytes).Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        try
        {
            return Convert.ToInt32(s, 8);
        }
        catch
        {
            return fallback;
        }
    }

    private static long ParseOctalLong(ReadOnlySpan<byte> bytes, long fallback)
    {
        var s = ReadString(bytes).Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        try
        {
            return Convert.ToInt64(s, 8);
        }
        catch
        {
            return fallback;
        }
    }

    private static void SkipAligned(Stream stream, ulong size)
    {
        var aligned = ((long)size + 511) & ~511L;
        if (aligned <= 0) return;

        if (stream.CanSeek)
        {
            stream.Seek(aligned, SeekOrigin.Current);
            return;
        }

        Span<byte> buffer = stackalloc byte[4096];
        long remaining = aligned;
        while (remaining > 0)
        {
            var n = stream.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (n <= 0) throw new EndOfStreamException("Unexpected EOF while skipping tar payload");
            remaining -= n;
        }
    }

    private static string NormalizeEntryPath(string raw)
    {
        var path = raw.Replace('\\', '/').Trim();
        path = path.TrimStart('/');
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        if (path == ".") return string.Empty;
        if (path.EndsWith('/')) path = path.TrimEnd('/');
        return path;
    }
}
