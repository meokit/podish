using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Fiberish.VFS;

namespace FiberPod;

internal sealed class ImageArchiveService
{
    private readonly string _projectRoot;
    private readonly string _fiberpodDir;
    private readonly string _ociImagesDir;
    private readonly string _eventsPath;

    public ImageArchiveService(string projectRoot)
    {
        EnsureFileSystemsRegistered();
        _projectRoot = projectRoot;
        _fiberpodDir = Path.Combine(projectRoot, ".fiberpod");
        _ociImagesDir = Path.Combine(_fiberpodDir, "oci", "images");
        _eventsPath = Path.Combine(_fiberpodDir, "events.jsonl");
        Directory.CreateDirectory(_ociImagesDir);
    }

    public void Save(string outputArchive, IReadOnlyList<string> imageReferences)
    {
        if (imageReferences.Count == 0)
            throw new InvalidOperationException("at least one image is required");

        var outputDir = Path.GetDirectoryName(outputArchive);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var tmp = outputArchive + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);

        var manifest = new SaveArchiveManifest(new List<SaveArchiveImage>());
        using (var fs = File.Create(tmp))
        using (var writer = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false))
        {
            var seenBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imageRef in imageReferences)
            {
                var (storeDir, safeName) = ResolveStoreDirForImage(imageRef);
                var imagePath = Path.Combine(storeDir, "image.json");
                if (!File.Exists(imagePath))
                    throw new FileNotFoundException($"image metadata not found: {imagePath}");

                var storedImage = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath))
                                  ?? throw new InvalidOperationException($"invalid image metadata: {imagePath}");
                manifest.Images.Add(new SaveArchiveImage(imageRef, safeName));

                writer.WriteEntry(imagePath, $"images/{safeName}/image.json");
                foreach (var layer in storedImage.Layers)
                {
                    if (!File.Exists(layer.BlobPath))
                        throw new FileNotFoundException($"layer blob missing: {layer.BlobPath}");
                    if (!File.Exists(layer.IndexPath))
                        throw new FileNotFoundException($"layer index missing: {layer.IndexPath}");

                    var digestHex = DigestHex(layer.Digest);
                    if (seenBlobs.Add(digestHex))
                        writer.WriteEntry(layer.BlobPath, $"blobs/{digestHex}.tar");
                    if (seenIndexes.Add(digestHex))
                        writer.WriteEntry(layer.IndexPath, $"indexes/{digestHex}.json");
                }
            }

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, "manifest.json")
            {
                DataStream = new MemoryStream(manifestBytes, writable: false)
            };
            writer.WriteEntry(manifestEntry);
        }

        File.Move(tmp, outputArchive, overwrite: true);
    }

    public IReadOnlyList<string> Load(string inputArchive)
    {
        if (!File.Exists(inputArchive))
            throw new FileNotFoundException($"archive not found: {inputArchive}");

        var extractedRoot = Path.Combine(Path.GetTempPath(), $"fiberpod-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractedRoot);
        try
        {
            ExtractTarToDirectory(inputArchive, extractedRoot);
            var imagesRoot = Path.Combine(extractedRoot, "images");
            if (!Directory.Exists(imagesRoot))
                throw new InvalidOperationException("archive missing images/ directory");

            var loaded = new List<string>();
            foreach (var imageDir in Directory.EnumerateDirectories(imagesRoot))
            {
                var safeName = Path.GetFileName(imageDir);
                var imagePath = Path.Combine(imageDir, "image.json");
                if (!File.Exists(imagePath))
                    continue;

                var original = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath))
                               ?? throw new InvalidOperationException($"invalid image metadata: {imagePath}");
                var targetStore = Path.Combine(_ociImagesDir, safeName);
                var targetBlobs = Path.Combine(targetStore, "blobs");
                var targetIndexes = Path.Combine(targetStore, "indexes");
                Directory.CreateDirectory(targetBlobs);
                Directory.CreateDirectory(targetIndexes);

                var rewrittenLayers = new List<OciStoredLayer>(original.Layers.Count);
                foreach (var layer in original.Layers)
                {
                    var digestHex = DigestHex(layer.Digest);
                    var srcBlob = Path.Combine(extractedRoot, "blobs", $"{digestHex}.tar");
                    var srcIndex = Path.Combine(extractedRoot, "indexes", $"{digestHex}.json");
                    if (!File.Exists(srcBlob))
                        throw new InvalidOperationException($"archive missing blob for digest {layer.Digest}");
                    if (!File.Exists(srcIndex))
                        throw new InvalidOperationException($"archive missing index for digest {layer.Digest}");

                    var dstBlob = Path.Combine(targetBlobs, $"{digestHex}.tar");
                    var dstIndex = Path.Combine(targetIndexes, $"{digestHex}.json");
                    File.Copy(srcBlob, dstBlob, overwrite: true);
                    File.Copy(srcIndex, dstIndex, overwrite: true);
                    rewrittenLayers.Add(layer with { BlobPath = dstBlob, IndexPath = dstIndex });
                }

                var rewritten = original with
                {
                    StoreDirectory = targetStore,
                    Layers = rewrittenLayers
                };
                Directory.CreateDirectory(targetStore);
                File.WriteAllText(Path.Combine(targetStore, "image.json"),
                    JsonSerializer.Serialize(rewritten, JsonOptions));
                loaded.Add(rewritten.ImageReference);
            }

            return loaded;
        }
        finally
        {
            try { Directory.Delete(extractedRoot, recursive: true); } catch { }
        }
    }

    public string Import(string sourceTar, string imageReference)
    {
        if (!File.Exists(sourceTar))
            throw new FileNotFoundException($"import source not found: {sourceTar}");
        if (string.IsNullOrWhiteSpace(imageReference))
            imageReference = "localhost/fiberpod-import:latest";

        var safeName = ToSafeImageName(imageReference);
        var storeDir = Path.Combine(_ociImagesDir, safeName);
        var blobsDir = Path.Combine(storeDir, "blobs");
        var indexesDir = Path.Combine(storeDir, "indexes");
        Directory.CreateDirectory(blobsDir);
        Directory.CreateDirectory(indexesDir);

        var digestHex = Sha256HexOfFile(sourceTar);
        var digest = "sha256:" + digestHex;
        var blobPath = Path.Combine(blobsDir, $"{digestHex}.tar");
        File.Copy(sourceTar, blobPath, overwrite: true);

        LayerIndex index;
        using (var tarStream = File.OpenRead(blobPath))
            index = OciLayerIndexBuilder.BuildFromTar(tarStream, digest);
        var indexPath = Path.Combine(indexesDir, $"{digestHex}.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(index.Entries.Values.ToList(), JsonOptions));

        var (registry, repository, tag) = ParseImageReference(imageReference);
        var layerSize = new FileInfo(blobPath).Length;
        var image = new OciStoredImage(
            imageReference,
            registry,
            repository,
            tag,
            digest,
            storeDir,
            new[]
            {
                new OciStoredLayer(
                    digest,
                    "application/vnd.oci.image.layer.v1.tar",
                    layerSize,
                    blobPath,
                    indexPath)
            });

        File.WriteAllText(Path.Combine(storeDir, "image.json"),
            JsonSerializer.Serialize(image, JsonOptions));
        return image.ImageReference;
    }

    public void Export(string containerId, string outputArchive)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            throw new InvalidOperationException("container id is required");

        var imageRef = ResolveImageReferenceForContainer(containerId);
        if (string.IsNullOrWhiteSpace(imageRef))
            throw new InvalidOperationException($"container not found: {containerId}");

        var containerDir = Path.Combine(_fiberpodDir, "containers", containerId);
        var upperStore = Path.Combine(containerDir, "silk-upper");
        var outputDir = Path.GetDirectoryName(outputArchive);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        var tmp = outputArchive + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);

        var (rootDentry, exportMount, disposer) = OpenExportView(imageRef, upperStore);
        try
        {
        using (var fs = File.Create(tmp))
        using (var writer = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false))
        {
            WriteDentryTreeToTar(writer, rootDentry, exportMount, "");
        }
        }
        finally
        {
            disposer.Dispose();
        }

        File.Move(tmp, outputArchive, overwrite: true);
    }

    private (Dentry Root, Mount ExportMount, IDisposable Disposer) OpenExportView(string imageRef, string upperStore)
    {
        var (storeDir, _) = ResolveStoreDirForImage(imageRef);
        var imagePath = Path.Combine(storeDir, "image.json");
        if (!File.Exists(imagePath))
            throw new InvalidOperationException($"container image metadata missing: {imagePath}");
        var image = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath))
                    ?? throw new InvalidOperationException($"invalid image metadata: {imagePath}");

        var (lowerSb, lowerProvider) = BuildLowerSuperBlock(image);

        if (!Directory.Exists(upperStore))
        {
            var mount = new Mount(lowerSb, lowerSb.Root);
            return (lowerSb.Root, mount, new CompositeDisposable(lowerProvider));
        }

        var silkType = FileSystemRegistry.Get("silkfs")
                       ?? throw new InvalidOperationException("silkfs is not registered");
        var overlayType = FileSystemRegistry.Get("overlay")
                          ?? throw new InvalidOperationException("overlay is not registered");

        var upperSb = silkType.FileSystem.ReadSuper(silkType, 0, upperStore, null);
        var overlaySb = overlayType.FileSystem.ReadSuper(overlayType, 0, "export-overlay", new OverlayMountOptions
        {
            Lower = lowerSb,
            Upper = upperSb
        });
        var mountOverlay = new Mount(overlaySb, overlaySb.Root);
        return (overlaySb.Root, mountOverlay, new CompositeDisposable(lowerProvider));
    }

    private (SuperBlock Lower, IDisposable Provider) BuildLowerSuperBlock(OciStoredImage image)
    {
        var layerIndexes = new List<IReadOnlyList<LayerIndexEntry>>(image.Layers.Count);
        var digestToBlobPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in image.Layers)
        {
            if (!File.Exists(layer.IndexPath))
                throw new InvalidOperationException($"missing layer index file: {layer.IndexPath}");
            if (!File.Exists(layer.BlobPath))
                throw new InvalidOperationException($"missing layer blob file: {layer.BlobPath}");
            var entries = JsonSerializer.Deserialize<List<LayerIndexEntry>>(File.ReadAllText(layer.IndexPath))
                          ?? throw new InvalidOperationException($"invalid layer index JSON: {layer.IndexPath}");
            layerIndexes.Add(entries);
            digestToBlobPath[layer.Digest] = layer.BlobPath;
        }

        var merged = MergeLayerIndexes(layerIndexes);
        var layerType = FileSystemRegistry.Get("layerfs");
        if (layerType == null)
            throw new InvalidOperationException("layerfs is not registered");
        var provider = new TarBlobLayerContentProvider(digestToBlobPath);
        var lowerSb = layerType.FileSystem.ReadSuper(layerType, 0, "layer-lower",
            new LayerMountOptions { Index = merged, ContentProvider = provider });
        return (lowerSb, provider);
    }

    private static void WriteDentryTreeToTar(TarWriter writer, Dentry root, Mount mount, string relPath)
    {
        var inode = root.Inode;
        if (inode == null)
            return;

        if (!string.IsNullOrEmpty(relPath))
        {
            if (inode.Type == InodeType.Directory)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relPath + "/"));
            }
            else if (inode.Type == InodeType.Symlink)
            {
                var link = new PaxTarEntry(TarEntryType.SymbolicLink, relPath)
                {
                    LinkName = inode.Readlink()
                };
                writer.WriteEntry(link);
                return;
            }
            else if (inode.Type == InodeType.File)
            {
                var bytes = ReadAllInodeBytes(root, mount);
                var file = new PaxTarEntry(TarEntryType.RegularFile, relPath)
                {
                    DataStream = new MemoryStream(bytes, writable: false)
                };
                writer.WriteEntry(file);
                return;
            }
            else
            {
                return;
            }
        }

        if (inode.Type != InodeType.Directory)
            return;

        var children = inode.GetEntries()
            .Where(e => e.Name != "." && e.Name != "..")
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var child in children)
        {
            var dentry = inode.Lookup(child.Name);
            if (dentry == null || dentry.Inode == null)
                continue;
            var childRel = string.IsNullOrEmpty(relPath) ? child.Name : relPath + "/" + child.Name;
            WriteDentryTreeToTar(writer, dentry, mount, childRel);
        }
    }

    private static byte[] ReadAllInodeBytes(Dentry dentry, Mount mount)
    {
        var inode = dentry.Inode;
        if (inode == null || inode.Type != InodeType.File)
            return Array.Empty<byte>();

        var lf = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        try
        {
            using var ms = new MemoryStream();
            var offset = 0L;
            var buf = new byte[64 * 1024];
            while (true)
            {
                var n = inode.Read(lf, buf, offset);
                if (n <= 0)
                    break;
                ms.Write(buf, 0, n);
                offset += n;
            }

            return ms.ToArray();
        }
        finally
        {
            lf.Close();
        }
    }

    private (string StoreDir, string SafeName) ResolveStoreDirForImage(string imageReferenceOrPath)
    {
        if (Directory.Exists(imageReferenceOrPath))
        {
            var imagePath = Path.Combine(imageReferenceOrPath, "image.json");
            if (File.Exists(imagePath))
                return (imageReferenceOrPath, Path.GetFileName(imageReferenceOrPath));
        }

        var safeName = ToSafeImageName(imageReferenceOrPath);
        return (Path.Combine(_ociImagesDir, safeName), safeName);
    }

    private string? ResolveImageReferenceForContainer(string containerId)
    {
        if (!File.Exists(_eventsPath))
            return null;

        string? image = null;
        foreach (var line in File.ReadLines(_eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            ContainerEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ContainerEvent>(line);
            }
            catch
            {
                continue;
            }

            if (evt == null)
                continue;
            if (evt.ContainerId == containerId && evt.Type == "container-create")
                image = evt.Image;
        }

        return image;
    }

    private static void ExtractTarToDirectory(string tarPath, string outputDir)
    {
        using var fs = File.OpenRead(tarPath);
        using var reader = new TarReader(fs);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            var path = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (path.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"unsafe archive entry path: {entry.Name}");

            var target = Path.Combine(outputDir, path);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    if (entry.DataStream == null)
                        break;
                    using (var outStream = File.Create(target))
                        entry.DataStream.CopyTo(outStream);
                    break;
            }
        }
    }

    private static byte[] ReadLayerFileBytes(LayerIndexEntry entry, IReadOnlyDictionary<string, string> digestToBlob)
    {
        if (entry.InlineData != null)
            return entry.InlineData;
        if (entry.DataOffset < 0 || string.IsNullOrWhiteSpace(entry.BlobDigest))
            return Array.Empty<byte>();
        if (!digestToBlob.TryGetValue(entry.BlobDigest, out var blobPath))
            throw new InvalidOperationException($"blob path missing for digest {entry.BlobDigest}");

        using var stream = File.OpenRead(blobPath);
        stream.Seek(entry.DataOffset, SeekOrigin.Begin);
        var size = checked((int)entry.Size);
        var bytes = new byte[size];
        var total = 0;
        while (total < size)
        {
            var n = stream.Read(bytes, total, size - total);
            if (n <= 0)
                break;
            total += n;
        }
        if (total == size)
            return bytes;
        return bytes[..total];
    }

    private static string ToSafeImageName(string imageReference)
    {
        return imageReference.Replace("/", "_").Replace(":", "_");
    }

    private static string DigestHex(string digest)
    {
        return digest.StartsWith("sha256:", StringComparison.Ordinal)
            ? digest["sha256:".Length..]
            : digest;
    }

    private static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (string Registry, string Repository, string Tag) ParseImageReference(string imageReference)
    {
        var firstSlashIndex = imageReference.IndexOf('/');
        if (firstSlashIndex == -1 || !imageReference[..firstSlashIndex].Contains('.'))
            imageReference = "docker.io/" + imageReference;
        firstSlashIndex = imageReference.IndexOf('/');
        var registry = imageReference[..firstSlashIndex];

        var rest = imageReference[(firstSlashIndex + 1)..];
        var tag = "latest";
        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex > 0)
        {
            tag = rest[(colonIndex + 1)..];
            rest = rest[..colonIndex];
        }

        return (registry, rest, tag);
    }

    private static LayerIndex MergeLayerIndexes(IReadOnlyList<IReadOnlyList<LayerIndexEntry>> layers)
    {
        var merged = new Dictionary<string, LayerIndexEntry>(StringComparer.Ordinal)
        {
            ["/"] = new LayerIndexEntry("/", InodeType.Directory, 0x1ED)
        };

        foreach (var layer in layers)
        {
            foreach (var entry in layer)
            {
                var path = NormalizeAbsolutePath(entry.Path);
                if (path == "/") continue;

                var parent = ParentPath(path);
                var name = BaseName(path);
                if (name == ".wh..wh..opq")
                {
                    RemoveAllChildren(merged, parent);
                    continue;
                }

                if (name.StartsWith(".wh.", StringComparison.Ordinal) && name.Length > 4)
                {
                    var hiddenName = name[4..];
                    var hiddenPath = parent == "/" ? "/" + hiddenName : parent + "/" + hiddenName;
                    RemovePathWithDescendants(merged, hiddenPath);
                    continue;
                }

                merged[path] = entry with { Path = path };
            }
        }

        var index = new LayerIndex();
        foreach (var entry in merged.Values
                     .Where(e => e.Path != "/")
                     .OrderBy(e => e.Path.Count(c => c == '/'))
                     .ThenBy(e => e.Path, StringComparer.Ordinal))
            index.AddEntry(entry);
        return index;
    }

    private static void RemoveAllChildren(Dictionary<string, LayerIndexEntry> merged, string parentPath)
    {
        var prefix = parentPath == "/" ? "/" : parentPath + "/";
        var keys = merged.Keys.Where(k => k != "/" && k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var k in keys)
            merged.Remove(k);
    }

    private static void RemovePathWithDescendants(Dictionary<string, LayerIndexEntry> merged, string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        merged.Remove(normalized);
        var prefix = normalized == "/" ? "/" : normalized + "/";
        var keys = merged.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var k in keys)
            merged.Remove(k);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    private static string ParentPath(string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        if (normalized == "/") return "/";
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalized[..lastSlash];
    }

    private static string BaseName(string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        if (normalized == "/") return "/";
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static void EnsureFileSystemsRegistered()
    {
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "tmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "devtmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "proc", FileSystem = new ProcFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "overlay", FileSystem = new OverlayFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "layerfs", FileSystem = new LayerFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "silkfs", FileSystem = new SilkFileSystem() });
    }
}

internal sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
{
    private readonly IDisposable[] _disposables = disposables;
    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
    }
}

internal sealed class TarBlobLayerContentProvider : ILayerContentProvider, IDisposable
{
    private readonly Dictionary<string, string> _digestToBlobPath;
    private readonly Dictionary<string, FileStream> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public TarBlobLayerContentProvider(Dictionary<string, string> digestToBlobPath)
    {
        _digestToBlobPath = digestToBlobPath;
    }

    public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (entry.Type != InodeType.File) return true;

        if (entry.InlineData != null)
        {
            if (offset >= entry.InlineData.Length) return true;
            var remaining = entry.InlineData.Length - (int)offset;
            var toCopy = Math.Min(buffer.Length, remaining);
            entry.InlineData.AsSpan((int)offset, toCopy).CopyTo(buffer);
            bytesRead = toCopy;
            return true;
        }

        if (entry.DataOffset < 0 || string.IsNullOrWhiteSpace(entry.BlobDigest))
            return false;
        if (!_digestToBlobPath.TryGetValue(entry.BlobDigest, out var blobPath))
            return false;
        if (offset < 0) return false;
        if ((ulong)offset >= entry.Size) return true;

        var remainingInEntry = (long)entry.Size - offset;
        if (remainingInEntry <= 0) return true;
        var maxReadable = (int)Math.Min(buffer.Length, remainingInEntry);
        if (maxReadable <= 0) return true;

        lock (_lock)
        {
            if (!_streams.TryGetValue(entry.BlobDigest, out var stream))
            {
                stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _streams[entry.BlobDigest] = stream;
            }

            var start = entry.DataOffset + offset;
            if (start < 0 || start >= stream.Length)
                return true;

            stream.Seek(start, SeekOrigin.Begin);
            bytesRead = stream.Read(buffer[..maxReadable]);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _streams.Values)
                s.Dispose();
            _streams.Clear();
        }
    }
}

internal sealed record SaveArchiveManifest(List<SaveArchiveImage> Images);
internal sealed record SaveArchiveImage(string ImageReference, string SafeName);
