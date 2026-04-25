using System.IO.Compression;
using System.Text;
using Fiberish.VFS;

namespace Podish.Core;

internal static class RootfsArchiveLoader
{
    private const int TarBlockSize = 512;

    public static SuperBlock LoadTmpfsFromTar(Stream tarStream, DeviceNumberManager deviceNumbers, string sourceName)
    {
        var fsType = new FileSystemType { Name = "tmpfs", Factory = devMgr => new Tmpfs(devMgr) };
        return LoadFromTar(tarStream, deviceNumbers, fsType, sourceName);
    }

    public static SuperBlock LoadFromTar(Stream tarStream, DeviceNumberManager deviceNumbers, FileSystemType fsType,
        string sourceName, object? data = null)
    {
        if (tarStream == null) throw new ArgumentNullException(nameof(tarStream));
        if (!tarStream.CanRead) throw new ArgumentException("Tar stream must be readable.", nameof(tarStream));

        var sb = fsType.CreateFileSystem(deviceNumbers).ReadSuper(fsType, 0, sourceName, data);
        var root = sb.Root ?? throw new InvalidOperationException($"{fsType.Name} root was not created.");

        using var archiveStream = OpenPossiblyCompressedTarStream(tarStream);
        var pendingHardLinks = new List<(string Path, string Target)>();
        string? nextPathOverride = null;
        string? nextLinkOverride = null;

        while (true)
        {
            var header = ReadExactOrNull(archiveStream, TarBlockSize);
            if (header == null || IsZeroBlock(header))
                break;

            var entryName = ReadHeaderString(header, 0, 100);
            var prefix = ReadHeaderString(header, 345, 155);
            if (!string.IsNullOrEmpty(prefix))
                entryName = string.IsNullOrEmpty(entryName) ? prefix : $"{prefix}/{entryName}";

            var typeFlag = header[156];
            var size = ReadOctal(header, 124, 12);
            var mode = (int)(ReadOctal(header, 100, 8) is var parsedMode && parsedMode != 0 ? parsedMode : 0);
            var linkName = ReadHeaderString(header, 157, 100);

            var payloadBytes = size > 0 ? ReadExact(archiveStream, checked((int)size)) : Array.Empty<byte>();
            SkipPadding(archiveStream, size);

            switch ((char)(typeFlag == 0 ? (byte)'0' : typeFlag))
            {
                case 'x':
                case 'g':
                    ApplyPaxHeaders(payloadBytes, ref nextPathOverride, ref nextLinkOverride);
                    continue;
                case 'L':
                    nextPathOverride = ReadNullTerminated(payloadBytes);
                    continue;
                case 'K':
                    nextLinkOverride = ReadNullTerminated(payloadBytes);
                    continue;
            }

            var rawPath = nextPathOverride ?? entryName;
            var rawLink = nextLinkOverride ?? linkName;
            nextPathOverride = null;
            nextLinkOverride = null;

            var relativePath = NormalizeEntryPath(rawPath);
            if (string.IsNullOrEmpty(relativePath))
                continue;

            var effectiveMode = mode != 0 ? mode : IsDirectoryType(typeFlag) ? 0x1FF : 0x1A4;
            switch ((char)(typeFlag == 0 ? (byte)'0' : typeFlag))
            {
                case '5':
                    EnsureDirectory(root, relativePath, effectiveMode);
                    break;
                case '0':
                case '7':
                    CreateOrReplaceFile(root, relativePath, effectiveMode, payloadBytes);
                    break;
                case '2':
                    CreateSymlink(root, relativePath, rawLink ?? string.Empty);
                    break;
                case '1':
                    pendingHardLinks.Add((relativePath, NormalizeEntryPath(rawLink ?? string.Empty)));
                    break;
            }
        }

        foreach (var (path, target) in pendingHardLinks)
            CreateHardLink(root, path, target);

        return sb;
    }

    private static Stream OpenPossiblyCompressedTarStream(Stream stream)
    {
        if (!stream.CanSeek)
            return stream;

        var origin = stream.Position;
        Span<byte> header = stackalloc byte[2];
        var read = stream.Read(header);
        stream.Position = origin;

        if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
            return new GZipStream(stream, CompressionMode.Decompress, true);

        return stream;
    }

    private static bool IsDirectoryType(byte typeFlag)
    {
        return (char)(typeFlag == 0 ? (byte)'0' : typeFlag) == '5';
    }

    private static void ApplyPaxHeaders(byte[] payload, ref string? pathOverride, ref string? linkOverride)
    {
        var text = Encoding.UTF8.GetString(payload);
        var index = 0;
        while (index < text.Length)
        {
            var spaceIndex = text.IndexOf(' ', index);
            if (spaceIndex <= index)
                break;

            if (!int.TryParse(text[index..spaceIndex], out var recordLength) || recordLength <= 0)
                break;

            var recordEnd = index + recordLength;
            if (recordEnd > text.Length)
                break;

            var record = text[(spaceIndex + 1)..recordEnd];
            if (record.EndsWith('\n'))
                record = record[..^1];

            var equalsIndex = record.IndexOf('=');
            if (equalsIndex > 0)
            {
                var key = record[..equalsIndex];
                var value = record[(equalsIndex + 1)..];
                if (key == "path")
                    pathOverride = value;
                else if (key is "linkpath" or "SCHILY.linkpath")
                    linkOverride = value;
            }

            index = recordEnd;
        }
    }

    private static string ReadNullTerminated(byte[] payload)
    {
        var length = Array.IndexOf(payload, (byte)0);
        if (length < 0)
            length = payload.Length;
        return Encoding.UTF8.GetString(payload, 0, length);
    }

    private static string ReadHeaderString(byte[] header, int offset, int length)
    {
        var end = offset + length;
        var actualEnd = offset;
        while (actualEnd < end && header[actualEnd] != 0)
            actualEnd++;
        return Encoding.UTF8.GetString(header, offset, actualEnd - offset).Trim();
    }

    private static long ReadOctal(byte[] buffer, int offset, int length)
    {
        var end = offset + length;
        while (offset < end && (buffer[offset] == 0 || buffer[offset] == (byte)' '))
            offset++;

        long value = 0;
        for (var index = offset; index < end; index++)
        {
            var current = buffer[index];
            if (current is 0 or (byte)' ' or (byte)'\0')
                break;
            if (current < '0' || current > '7')
                break;
            value = (value << 3) + (current - '0');
        }

        return value;
    }

    private static void SkipPadding(Stream stream, long size)
    {
        var padding = (TarBlockSize - size % TarBlockSize) % TarBlockSize;
        if (padding == 0)
            return;

        var skipped = ReadExact(stream, (int)padding);
        if (skipped.Length != padding)
            throw new EndOfStreamException("Unexpected end of tar archive while skipping padding.");
    }

    private static byte[]? ReadExactOrNull(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                if (offset == 0)
                    return null;
                throw new EndOfStreamException("Unexpected end of tar archive.");
            }

            offset += read;
        }

        return buffer;
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of tar archive.");
            offset += read;
        }

        return buffer;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        for (var index = 0; index < block.Length; index++)
            if (block[index] != 0)
                return false;

        return true;
    }

    private static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        normalized = normalized.Trim('/');
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sanitized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
                throw new InvalidOperationException($"Parent traversal is not allowed in rootfs tar entry '{path}'.");
            sanitized.Add(part);
        }

        return string.Join('/', sanitized);
    }

    private static Dentry EnsureDirectory(Dentry root, string relativePath, int mode)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = current.Inode!.Lookup(segment);
            if (existing != null)
            {
                if (existing.Inode?.Type != InodeType.Directory)
                    throw new InvalidOperationException($"Path segment '{segment}' is not a directory.");
                current = existing;
                continue;
            }

            var created = new Dentry(FsName.FromString(segment), null, current, current.SuperBlock);
            current.Inode.Mkdir(created, mode, 0, 0);
            current = created;
        }

        return current;
    }

    private static void CreateOrReplaceFile(Dentry root, string relativePath, int mode, byte[] data)
    {
        var (parent, name) = ResolveParent(root, relativePath);
        var existing = parent.Inode!.Lookup(name);
        if (existing != null)
        {
            if (existing.Inode?.Type == InodeType.Directory)
                throw new InvalidOperationException($"Cannot replace directory '{relativePath}' with file.");
            parent.Inode.Unlink(name);
        }

        var fileDentry = new Dentry(FsName.FromString(name), null, parent, parent.SuperBlock);
        parent.Inode.Create(fileDentry, mode, 0, 0);

        if (data.Length == 0)
            return;

        var file = new LinuxFile(fileDentry, FileFlags.O_WRONLY, null!);
        try
        {
            var rc = fileDentry.Inode!.WriteFromHost(null, file, data, 0);
            if (rc < 0)
                throw new IOException($"Failed to write rootfs file '{relativePath}': rc={rc}");
        }
        finally
        {
            file.Close();
        }
    }

    private static void CreateSymlink(Dentry root, string relativePath, string target)
    {
        var (parent, name) = ResolveParent(root, relativePath);
        var existing = parent.Inode!.Lookup(name);
        if (existing != null)
        {
            if (existing.Inode?.Type == InodeType.Directory)
                throw new InvalidOperationException($"Cannot replace directory '{relativePath}' with symlink.");
            parent.Inode.Unlink(name);
        }

        var symlinkDentry = new Dentry(FsName.FromString(name), null, parent, parent.SuperBlock);
        parent.Inode.Symlink(symlinkDentry, target, 0, 0);
    }

    private static void CreateHardLink(Dentry root, string relativePath, string targetPath)
    {
        if (string.IsNullOrEmpty(targetPath))
            throw new InvalidOperationException($"Hard link target is empty for '{relativePath}'.");

        var target = LookupPath(root, targetPath)
                     ?? throw new FileNotFoundException($"Hard link target not found: {targetPath}");
        if (target.Inode == null)
            throw new InvalidOperationException($"Hard link target missing inode: {targetPath}");

        var (parent, name) = ResolveParent(root, relativePath);
        var existing = parent.Inode!.Lookup(name);
        if (existing != null)
        {
            if (existing.Inode?.Type == InodeType.Directory)
                throw new InvalidOperationException($"Cannot replace directory '{relativePath}' with hard link.");
            parent.Inode.Unlink(name);
        }

        var linkDentry = new Dentry(FsName.FromString(name), null, parent, parent.SuperBlock);
        parent.Inode.Link(linkDentry, target.Inode);
    }

    private static Dentry? LookupPath(Dentry root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Inode!.Lookup(segment);
            if (next == null)
                return null;
            current = next;
        }

        return current;
    }

    private static (Dentry Parent, string Name) ResolveParent(Dentry root, string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        if (lastSlash < 0)
            return (root, relativePath);

        var parentPath = relativePath[..lastSlash];
        var name = relativePath[(lastSlash + 1)..];
        return (EnsureDirectory(root, parentPath, 0x1FF), name);
    }
}
