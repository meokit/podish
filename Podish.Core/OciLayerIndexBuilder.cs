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
        var pendingHardLinks = new List<(string Path, string Target, int Mode, int Uid, int Gid, DateTime MTime)>();
        string? nextPathOverride = null;
        string? nextLinkOverride = null;
        long? nextSizeOverride = null;
        int? nextUidOverride = null;
        int? nextGidOverride = null;

        while (true)
        {
            if (!TryReadExactly(tarStream, header)) break;
            if (IsZeroBlock(header)) break;

            var name = ReadString(header.AsSpan(0, 100));
            var mode = ParseOctal(header.AsSpan(100, 8), 0x1A4);
            var uid = nextUidOverride ?? ParseOctal(header.AsSpan(108, 8), 0);
            var gid = nextGidOverride ?? ParseOctal(header.AsSpan(116, 8), 0);
            var size = (ulong)(nextSizeOverride ?? ParseOctalLong(header.AsSpan(124, 12), 0));
            var mtimeUnix = ParseOctalLong(header.AsSpan(136, 12), 0);
            var typeFlag = header[156];
            var linkName = ReadString(header.AsSpan(157, 100));
            var prefix = ReadString(header.AsSpan(345, 155));

            if (typeFlag is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K')
            {
                var payloadLen = checked((int)size);
                var payload = payloadLen > 0 ? new byte[payloadLen] : Array.Empty<byte>();
                if (payloadLen > 0)
                    ReadExactly(tarStream, payload);
                SkipPadding(tarStream, size);

                switch (typeFlag)
                {
                    case (byte)'x':
                    case (byte)'g':
                        ApplyPaxHeaders(payload, ref nextPathOverride, ref nextLinkOverride,
                            ref nextSizeOverride, ref nextUidOverride, ref nextGidOverride);
                        break;
                    case (byte)'L':
                        nextPathOverride = ReadNullTerminated(payload);
                        break;
                    case (byte)'K':
                        nextLinkOverride = ReadNullTerminated(payload);
                        break;
                }

                continue;
            }

            if (nextPathOverride != null)
            {
                name = nextPathOverride;
            }
            else if (!string.IsNullOrEmpty(prefix))
            {
                name = $"{prefix}/{name}";
            }

            if (nextLinkOverride != null)
                linkName = nextLinkOverride;

            nextPathOverride = null;
            nextLinkOverride = null;
            nextSizeOverride = null;
            nextUidOverride = null;
            nextGidOverride = null;

            name = NormalizeEntryPath(name);
            var path = "/" + name;
            var mtime = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, mtimeUnix)).UtcDateTime;
            var dataOffset = tarStream.CanSeek ? tarStream.Position : -1;

            if (!string.IsNullOrEmpty(name))
            {
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
                    case (byte)'1': // hardlink
                        var targetPath = "/" + NormalizeEntryPath(linkName);
                        pendingHardLinks.Add((path, targetPath, mode, uid, gid, mtime));
                        break;
                    case (byte)'0':
                    case (byte)'7':
                    case 0: // regular file
                        index.AddEntry(new LayerIndexEntry(path, InodeType.File, mode, uid, gid, size, "",
                            mtime, mtime, mtime, null, dataOffset, blobDigest));
                        break;
                }
            }

            SkipAligned(tarStream, size);
        }

        foreach (var (path, targetPath, mode, uid, gid, mtime) in pendingHardLinks)
        {
            if (index.TryGetEntry(targetPath, out var target))
            {
                index.AddEntry(target with
                {
                    Path = path,
                    Name = default,
                    EncodedSymlinkTarget = null
                });
            }
            else
            {
                // Keep a fallback symlink-free zero entry only if target wasn't present in this layer.
                index.AddEntry(new LayerIndexEntry(path, InodeType.Symlink, mode, uid, gid, 0, targetPath,
                    mtime, mtime, mtime, null, -1, blobDigest));
            }
        }

        return index;
    }

    private static void ApplyPaxHeaders(byte[] payload, ref string? pathOverride, ref string? linkOverride,
        ref long? sizeOverride, ref int? uidOverride, ref int? gidOverride)
    {
        var text = Encoding.UTF8.GetString(payload);
        var pos = 0;
        while (pos < text.Length)
        {
            var spaceIdx = text.IndexOf(' ', pos);
            if (spaceIdx <= pos) break;
            if (!int.TryParse(text[pos..spaceIdx], out var recordLen) || recordLen <= 0) break;

            var recordEnd = pos + recordLen;
            if (recordEnd > text.Length) break;

            var record = text[(spaceIdx + 1)..recordEnd];
            if (record.EndsWith('\n')) record = record[..^1];

            var eqIdx = record.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = record[..eqIdx];
                var value = record[(eqIdx + 1)..];
                switch (key)
                {
                    case "path":
                        pathOverride = value;
                        break;
                    case "linkpath":
                    case "SCHILY.linkpath":
                        linkOverride = value;
                        break;
                    case "size":
                        if (long.TryParse(value, out var parsedSize) && parsedSize >= 0)
                            sizeOverride = parsedSize;
                        break;
                    case "uid":
                        if (int.TryParse(value, out var parsedUid) && parsedUid >= 0)
                            uidOverride = parsedUid;
                        break;
                    case "gid":
                        if (int.TryParse(value, out var parsedGid) && parsedGid >= 0)
                            gidOverride = parsedGid;
                        break;
                }
            }

            pos = recordEnd;
        }
    }

    private static string ReadNullTerminated(byte[] payload)
    {
        var end = Array.IndexOf(payload, (byte)0);
        if (end < 0) end = payload.Length;
        return Encoding.UTF8.GetString(payload, 0, end);
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n <= 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException("Unexpected EOF while reading tar");
            }

            total += n;
        }

        return true;
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
        if (bytes.Length > 0 && (bytes[0] & 0x80) != 0)
        {
            // GNU base-256 big-endian integer encoding.
            long value = 0;
            for (var i = 1; i < bytes.Length; i++)
                value = (value << 8) | bytes[i];
            return value;
        }

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

    private static void SkipPadding(Stream stream, ulong size)
    {
        var padding = (512L - (long)(size % 512)) % 512L;
        if (padding <= 0) return;
        SkipBytes(stream, padding);
    }

    private static void SkipAligned(Stream stream, ulong size)
    {
        var aligned = ((long)size + 511) & ~511L;
        if (aligned <= 0) return;
        SkipBytes(stream, aligned);
    }

    private static void SkipBytes(Stream stream, long count)
    {
        if (count <= 0) return;

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> buffer = stackalloc byte[4096];
        var remaining = count;
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
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        path = path.TrimStart('/');
        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);
        if (path == ".") return string.Empty;
        if (path.EndsWith('/')) path = path.TrimEnd('/');
        return path;
    }
}
