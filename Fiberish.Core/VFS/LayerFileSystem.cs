using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

public class LayerFileSystem : FileSystem
{
    public LayerFileSystem(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "layerfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        if (data is not LayerMountOptions options)
            throw new ArgumentException("LayerFS requires LayerMountOptions in data");

        var index = options.Index;
        if (index == null)
        {
            if (options.Root == null)
                throw new ArgumentException(
                    "LayerFS requires either LayerMountOptions.Index or LayerMountOptions.Root");
            index = LayerIndex.FromNodeTree(options.Root);
        }

        var contentProvider = options.ContentProvider ?? new InMemoryLayerContentProvider();
        var sb = new LayerSuperBlock(fsType, index, contentProvider, DevManager);
        sb.Root = new Dentry("/", sb.GetOrCreateInode("/"), null, sb);
        sb.Root.Parent = sb.Root;
        return sb;
    }
}

public class LayerMountOptions
{
    public LayerNode? Root { get; init; }
    public LayerIndex? Index { get; init; }
    public ILayerContentProvider? ContentProvider { get; init; }
}

public interface ILayerContentProvider
{
    bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead);
}

public sealed class InMemoryLayerContentProvider : ILayerContentProvider
{
    public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (entry.Type != InodeType.File) return true;
        if (entry.InlineData == null) return false;
        if (offset >= entry.InlineData.Length) return true;

        var remaining = entry.InlineData.Length - (int)offset;
        var toCopy = Math.Min(buffer.Length, remaining);
        entry.InlineData.AsSpan((int)offset, toCopy).CopyTo(buffer);
        bytesRead = toCopy;
        return true;
    }
}

public sealed class LayerIndex
{
    private readonly Dictionary<string, Dictionary<string, string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayerIndexEntry> _entries = new(StringComparer.Ordinal);

    public LayerIndex()
    {
        AddEntry(new LayerIndexEntry("/", InodeType.Directory, 0x1ED)); // 0755
    }

    public IReadOnlyDictionary<string, LayerIndexEntry> Entries => _entries;

    public void AddEntry(LayerIndexEntry entry)
    {
        var normalizedPath = NormalizePath(entry.Path);
        var normalizedEntry = entry with { Path = normalizedPath };
        _entries[normalizedPath] = normalizedEntry;

        if (normalizedEntry.Type == InodeType.Directory)
            _children.TryAdd(normalizedPath, new Dictionary<string, string>(StringComparer.Ordinal));

        if (normalizedPath == "/") return;

        var parentPath = GetParentPath(normalizedPath);
        EnsureDirectory(parentPath);
        _children[parentPath][GetBaseName(normalizedPath)] = normalizedPath;
    }

    public bool TryGetEntry(string path, out LayerIndexEntry entry)
    {
        return _entries.TryGetValue(NormalizePath(path), out entry!);
    }

    public bool TryGetChildPath(string parentPath, string name, out string childPath)
    {
        childPath = string.Empty;
        var p = NormalizePath(parentPath);
        if (!_children.TryGetValue(p, out var map)) return false;
        return map.TryGetValue(name, out childPath!);
    }

    public IReadOnlyCollection<string> GetChildNames(string parentPath)
    {
        var p = NormalizePath(parentPath);
        if (!_children.TryGetValue(p, out var map)) return Array.Empty<string>();
        return map.Keys;
    }

    public static LayerIndex FromNodeTree(LayerNode root)
    {
        var index = new LayerIndex();
        index.AddFromNode(root, "/");
        return index;
    }

    private void AddFromNode(LayerNode node, string path)
    {
        var entry = new LayerIndexEntry(
            path,
            node.Type,
            node.Mode,
            node.Uid,
            node.Gid,
            node.Size,
            node.SymlinkTarget,
            node.MTime,
            node.ATime,
            node.CTime,
            node.Content);
        AddEntry(entry);

        if (node.Type != InodeType.Directory || node.Children == null) return;
        foreach (var child in node.Children.Values)
            AddFromNode(child, path == "/" ? "/" + child.Name : path + "/" + child.Name);
    }

    private void EnsureDirectory(string path)
    {
        var p = NormalizePath(path);
        if (_entries.TryGetValue(p, out var existing))
        {
            if (existing.Type != InodeType.Directory)
                throw new InvalidOperationException($"Path '{p}' already exists and is not a directory");
            _children.TryAdd(p, new Dictionary<string, string>(StringComparer.Ordinal));
            return;
        }

        AddEntry(new LayerIndexEntry(p, InodeType.Directory, 0x1ED));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    private static string GetParentPath(string path)
    {
        if (path == "/") return "/";
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        return path[..lastSlash];
    }

    private static string GetBaseName(string path)
    {
        if (path == "/") return "/";
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }
}

public sealed record LayerIndexEntry(
    string Path,
    InodeType Type,
    int Mode,
    int Uid = 0,
    int Gid = 0,
    ulong Size = 0,
    string SymlinkTarget = "",
    DateTime? MTime = null,
    DateTime? ATime = null,
    DateTime? CTime = null,
    byte[]? InlineData = null,
    long DataOffset = -1,
    string BlobDigest = "");

public class LayerSuperBlock : SuperBlock
{
    private readonly Dictionary<string, ulong> _inoByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayerInode> _inodeByPath = new(StringComparer.Ordinal);

    public LayerSuperBlock(FileSystemType fsType, LayerIndex index, ILayerContentProvider contentProvider,
        DeviceNumberManager? devManager = null) : base(devManager)
    {
        Type = fsType;
        Index = index;
        ContentProvider = contentProvider;
        foreach (var path in Index.Entries.Keys.OrderBy(static p => p, StringComparer.Ordinal))
            _inoByPath[path] = (ulong)(_inoByPath.Count + 1);
    }

    public LayerIndex Index { get; }
    public ILayerContentProvider ContentProvider { get; }

    public LayerInode GetOrCreateInode(string path)
    {
        var normalized = path.StartsWith('/') ? path : "/" + path;
        if (_inodeByPath.TryGetValue(normalized, out var inode))
        {
            if (!inode.IsCacheEvicted && !inode.IsFinalized)
                return inode;
            _inodeByPath.Remove(normalized);
        }

        if (!Index.TryGetEntry(normalized, out var entry))
            throw new InvalidOperationException($"Layer index entry not found: {normalized}");
        if (!_inoByPath.TryGetValue(normalized, out var ino))
            throw new InvalidOperationException($"Layer inode number missing: {normalized}");

        inode = new LayerInode(this, normalized, entry, ino);
        _inodeByPath[normalized] = inode;
        return inode;
    }

    public ulong GetStableIno(string path)
    {
        var normalized = path.StartsWith('/') ? path : "/" + path;
        if (!_inoByPath.TryGetValue(normalized, out var ino))
            throw new InvalidOperationException($"Layer inode number missing: {normalized}");
        return ino;
    }

    internal void UnregisterInode(string path, LayerInode inode)
    {
        var normalized = path.StartsWith('/') ? path : "/" + path;
        if (_inodeByPath.TryGetValue(normalized, out var existing) && ReferenceEquals(existing, inode))
            _inodeByPath.Remove(normalized);
    }
}

public sealed class LayerNode
{
    private LayerNode(string name, InodeType type, int mode)
    {
        Name = name;
        Type = type;
        Mode = mode;
        Children = type == InodeType.Directory
            ? new Dictionary<string, LayerNode>(StringComparer.Ordinal)
            : null;
    }

    public string Name { get; }
    public InodeType Type { get; }
    public int Mode { get; }
    public int Uid { get; init; }
    public int Gid { get; init; }
    public DateTime MTime { get; init; } = DateTime.UnixEpoch;
    public DateTime ATime { get; init; } = DateTime.UnixEpoch;
    public DateTime CTime { get; init; } = DateTime.UnixEpoch;
    public byte[] Content { get; init; } = [];
    public string SymlinkTarget { get; init; } = string.Empty;
    public Dictionary<string, LayerNode>? Children { get; }
    public ulong Size => (ulong)(Type == InodeType.File ? Content.Length : 0);

    public static LayerNode Directory(string name, int mode = 0x1ED)
    {
        return new LayerNode(name, InodeType.Directory, mode);
    }

    public static LayerNode File(string name, byte[] content, int mode = 0x1A4)
    {
        return new LayerNode(name, InodeType.File, mode) { Content = content };
    }

    public static LayerNode Symlink(string name, string target, int mode = 0x1FF)
    {
        return new LayerNode(name, InodeType.Symlink, mode) { SymlinkTarget = target };
    }

    public LayerNode AddChild(LayerNode child)
    {
        if (Children == null) throw new InvalidOperationException("Only directories can have children");
        Children[child.Name] = child;
        return this;
    }
}

public class LayerInode : Inode
{
    private readonly LayerIndexEntry _entry;
    private readonly string _path;

    public LayerInode(LayerSuperBlock sb, string path, LayerIndexEntry entry, ulong ino)
    {
        SuperBlock = sb;
        _path = path;
        _entry = entry;
        Ino = ino;
        Type = entry.Type;
        Mode = entry.Mode;
        Uid = entry.Uid;
        Gid = entry.Gid;
        Size = entry.Size;
        MTime = entry.MTime ?? DateTime.UnixEpoch;
        ATime = entry.ATime ?? DateTime.UnixEpoch;
        CTime = entry.CTime ?? DateTime.UnixEpoch;
        SetInitialLinkCount(ComputeInitialLinkCount(sb, path, entry), "LayerInode.ctor");
    }

    public override bool SupportsMmap => _entry.Type == InodeType.File;

    public override Dentry? Lookup(string name)
    {
        if (_entry.Type != InodeType.Directory) return null;

        var sb = (LayerSuperBlock)SuperBlock;
        if (!sb.Index.TryGetChildPath(_path, name, out var childPath)) return null;

        var parentDentry = Dentries.Count > 0 ? Dentries[0] : null;
        return new Dentry(name, sb.GetOrCreateInode(childPath), parentDentry, SuperBlock);
    }

    private static int ComputeInitialLinkCount(LayerSuperBlock sb, string path, LayerIndexEntry entry)
    {
        if (entry.Type != InodeType.Directory)
            return 1;

        var childDirCount = 0;
        foreach (var childName in sb.Index.GetChildNames(path))
        {
            if (!sb.Index.TryGetChildPath(path, childName, out var childPath)) continue;
            if (!sb.Index.TryGetEntry(childPath, out var childEntry)) continue;
            if (childEntry.Type == InodeType.Directory)
                childDirCount++;
        }

        return 2 + childDirCount;
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (offset < 0) return -(int)Errno.EINVAL;
        if (_entry.Type != InodeType.File) return 0;
        return ReadWithPageCache(linuxFile, buffer, offset, BackendRead);
    }

    public override string Readlink()
    {
        if (_entry.Type != InodeType.Symlink) throw new InvalidOperationException("Not a symlink");
        return _entry.SymlinkTarget;
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EROFS;
    }

    protected override int AopsReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        pageBuffer.Clear();
        if (request.Length == 0) return 0;
        if (_entry.Type != InodeType.File) return 0;
        var rc = BackendRead(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        return rc < 0 ? rc : 0;
    }

    protected override int AopsReadahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        if (_entry.Type != InodeType.File || request.PageCount <= 0 || Mapping == null) return 0;

        var sb = (LayerSuperBlock)SuperBlock;
        var page = new byte[LinuxConstants.PageSize];
        for (var i = 0; i < request.PageCount; i++)
        {
            var pageIndex = request.StartPageIndex + i;
            if (Mapping.PeekPage((uint)pageIndex) != IntPtr.Zero) continue;

            var ptr = Mapping.GetOrCreatePage((uint)pageIndex, p =>
            {
                page.AsSpan().Clear();
                var fileOffset = pageIndex * LinuxConstants.PageSize;
                if (!sb.ContentProvider.TryRead(_entry, fileOffset, page, out var readLen)) return false;
                unsafe
                {
                    var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                    dst.Clear();
                    if (readLen > 0) page.AsSpan(0, readLen).CopyTo(dst);
                }

                return true;
            }, out _, true, AllocationClass.Readahead);

            if (ptr == IntPtr.Zero) return 0;
        }

        return 0;
    }

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
    {
        return -(int)Errno.EROFS;
    }

    protected override int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        return -(int)Errno.EROFS;
    }

    protected override int AopsSetPageDirty(long pageIndex)
    {
        return -(int)Errno.EROFS;
    }

    public override int Truncate(long length)
    {
        return -(int)Errno.EROFS;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        if (_entry.Type != InodeType.Directory) return [];

        var sb = (LayerSuperBlock)SuperBlock;
        var names = sb.Index.GetChildNames(_path);
        var entries = new List<DirectoryEntry>(names.Count);
        foreach (var name in names)
        {
            if (!sb.Index.TryGetChildPath(_path, name, out var childPath)) continue;
            if (!sb.Index.TryGetEntry(childPath, out var childEntry)) continue;
            entries.Add(new DirectoryEntry
            {
                Name = name,
                Ino = sb.GetStableIno(childPath),
                Type = childEntry.Type
            });
        }

        return entries;
    }

    protected override void OnEvictCache()
    {
        ((LayerSuperBlock)SuperBlock).UnregisterInode(_path, this);
        base.OnEvictCache();
    }

    protected override void OnFinalizeDelete()
    {
        ((LayerSuperBlock)SuperBlock).UnregisterInode(_path, this);
        base.OnFinalizeDelete();
    }

    private int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        var sb = (LayerSuperBlock)SuperBlock;
        return sb.ContentProvider.TryRead(_entry, offset, buffer, out var n) ? n : -(int)Errno.EIO;
    }
}