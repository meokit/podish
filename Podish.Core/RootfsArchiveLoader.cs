using System.Formats.Tar;
using Fiberish.Native;
using Fiberish.VFS;

namespace Podish.Core;

internal static class RootfsArchiveLoader
{
    public static SuperBlock LoadTmpfsFromTar(Stream tarStream, DeviceNumberManager deviceNumbers, string sourceName)
    {
        if (tarStream == null) throw new ArgumentNullException(nameof(tarStream));
        if (!tarStream.CanRead) throw new ArgumentException("Tar stream must be readable.", nameof(tarStream));

        var fsType = new FileSystemType { Name = "tmpfs", Factory = devMgr => new Tmpfs(devMgr) };
        var sb = fsType.CreateFileSystem(deviceNumbers).ReadSuper(fsType, 0, sourceName, null);
        var root = sb.Root ?? throw new InvalidOperationException("tmpfs root was not created.");

        using var reader = new TarReader(tarStream, leaveOpen: true);
        var pendingHardLinks = new List<(string Path, string Target)>();

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            var relativePath = NormalizeEntryPath(entry.Name);
            if (string.IsNullOrEmpty(relativePath))
                continue;

            var mode = GetEntryMode(entry, entry.EntryType == TarEntryType.Directory ? 0x1FF : 0x1A4);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    EnsureDirectory(root, relativePath, mode);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    CreateOrReplaceFile(root, relativePath, mode, entry.DataStream);
                    break;

                case TarEntryType.SymbolicLink:
                    CreateSymlink(root, relativePath, entry.LinkName ?? string.Empty);
                    break;

                case TarEntryType.HardLink:
                    pendingHardLinks.Add((relativePath, NormalizeEntryPath(entry.LinkName ?? string.Empty)));
                    break;

                default:
                    break;
            }
        }

        foreach (var (path, target) in pendingHardLinks)
            CreateHardLink(root, path, target);

        return sb;
    }

    private static int GetEntryMode(TarEntry entry, int fallbackMode)
    {
        try
        {
            return Convert.ToInt32(entry.Mode);
        }
        catch
        {
            return fallbackMode;
        }
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

            var created = new Dentry(segment, null, current, current.SuperBlock);
            current.Inode.Mkdir(created, mode, 0, 0);
            current = created;
        }

        return current;
    }

    private static void CreateOrReplaceFile(Dentry root, string relativePath, int mode, Stream? dataStream)
    {
        var (parent, name) = ResolveParent(root, relativePath);
        var existing = parent.Inode!.Lookup(name);
        if (existing != null)
        {
            if (existing.Inode?.Type == InodeType.Directory)
                throw new InvalidOperationException($"Cannot replace directory '{relativePath}' with file.");
            parent.Inode.Unlink(name);
        }

        var fileDentry = new Dentry(name, null, parent, parent.SuperBlock);
        parent.Inode.Create(fileDentry, mode, 0, 0);

        if (dataStream == null)
            return;

        using var ms = new MemoryStream();
        dataStream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
            return;

        var file = new LinuxFile(fileDentry, FileFlags.O_WRONLY, null!);
        try
        {
            var rc = fileDentry.Inode!.Write(file, bytes, 0);
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

        var symlinkDentry = new Dentry(name, null, parent, parent.SuperBlock);
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

        var linkDentry = new Dentry(name, null, parent, parent.SuperBlock);
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
