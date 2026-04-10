using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fiberish.VFS;

namespace Podish.Core;

public sealed class ImageArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _eventsPath;
    private readonly string _fiberpodDir;
    private readonly string _ociImagesDir;

    public ImageArchiveService(string projectRoot)
    {
        EnsureFileSystemsRegistered();
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
        SaveOciArchive(tmp, imageReferences);
        File.Move(tmp, outputArchive, true);
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
            if (!File.Exists(Path.Combine(extractedRoot, "oci-layout")) ||
                !File.Exists(Path.Combine(extractedRoot, "index.json")))
                throw new InvalidOperationException("unsupported archive format: expected OCI archive layout");

            return LoadOciArchiveExtracted(extractedRoot);
        }
        finally
        {
            try
            {
                Directory.Delete(extractedRoot, true);
            }
            catch
            {
            }
        }
    }

    private void SaveOciArchive(string archivePath, IReadOnlyList<string> imageReferences)
    {
        var layoutRoot = Path.Combine(Path.GetTempPath(), $"fiberpod-oci-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(layoutRoot);
        try
        {
            var blobsRoot = Path.Combine(layoutRoot, "blobs", "sha256");
            Directory.CreateDirectory(blobsRoot);

            var indexDescriptors = new List<OciDescriptor>();
            var writtenBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imageRef in imageReferences)
            {
                var (storeDir, _) = ResolveStoreDirForImage(imageRef);
                var imagePath = Path.Combine(storeDir, "image.json");
                if (!File.Exists(imagePath))
                    throw new FileNotFoundException($"image metadata not found: {imagePath}");
                var storedImage =
                    JsonSerializer.Deserialize(File.ReadAllText(imagePath), PodishJsonContext.Default.OciStoredImage)
                    ?? throw new InvalidOperationException($"invalid image metadata: {imagePath}");

                var layerDescriptors = new List<OciDescriptor>(storedImage.Layers.Count);
                var diffIds = new List<string>(storedImage.Layers.Count);
                foreach (var layer in storedImage.Layers)
                {
                    var blobPath = OciStorePath.Resolve(storeDir, layer.BlobPath);
                    if (!File.Exists(blobPath))
                        throw new FileNotFoundException($"layer blob missing: {blobPath}");

                    var layerHex = Sha256HexOfFile(blobPath);
                    var layerDigest = "sha256:" + layerHex;
                    var dstBlob = Path.Combine(blobsRoot, layerHex);
                    if (writtenBlobs.Add(layerHex))
                        File.Copy(blobPath, dstBlob, true);

                    var size = new FileInfo(blobPath).Length;
                    layerDescriptors.Add(new OciDescriptor(
                        "application/vnd.oci.image.layer.v1.tar",
                        layerDigest,
                        size));
                    diffIds.Add(layerDigest);
                }

                var config = new OciImageConfig(
                    "386",
                    "linux",
                    new OciRootFs("layers", diffIds),
                    diffIds.Select(_ => new OciHistory("fiberpod save")).ToList());
                var configBytes = JsonSerializer.SerializeToUtf8Bytes(config, PodishJsonContext.Default.OciImageConfig);
                var configHex = Sha256Hex(configBytes);
                var configDigest = "sha256:" + configHex;
                var configPath = Path.Combine(blobsRoot, configHex);
                if (writtenBlobs.Add(configHex))
                    File.WriteAllBytes(configPath, configBytes);

                var manifest = new OciManifest(
                    2,
                    "application/vnd.oci.image.manifest.v1+json",
                    new OciDescriptor("application/vnd.oci.image.config.v1+json", configDigest,
                        configBytes.LongLength),
                    layerDescriptors);
                var manifestBytes =
                    JsonSerializer.SerializeToUtf8Bytes(manifest, PodishJsonContext.Default.OciManifest);
                var manifestHex = Sha256Hex(manifestBytes);
                var manifestDigest = "sha256:" + manifestHex;
                var manifestPath = Path.Combine(blobsRoot, manifestHex);
                if (writtenBlobs.Add(manifestHex))
                    File.WriteAllBytes(manifestPath, manifestBytes);

                indexDescriptors.Add(new OciDescriptor(
                    "application/vnd.oci.image.manifest.v1+json",
                    manifestDigest,
                    manifestBytes.LongLength,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["org.opencontainers.image.ref.name"] = imageRef
                    }));
            }

            var layout =
                JsonSerializer.SerializeToUtf8Bytes(new OciLayout("1.0.0"), PodishJsonContext.Default.OciLayout);
            File.WriteAllBytes(Path.Combine(layoutRoot, "oci-layout"), layout);
            var index = JsonSerializer.SerializeToUtf8Bytes(new OciIndex(2, indexDescriptors),
                PodishJsonContext.Default.OciIndex);
            File.WriteAllBytes(Path.Combine(layoutRoot, "index.json"), index);

            CreateTarFromDirectory(layoutRoot, archivePath);
        }
        finally
        {
            try
            {
                Directory.Delete(layoutRoot, true);
            }
            catch
            {
            }
        }
    }

    private IReadOnlyList<string> LoadOciArchiveExtracted(string extractedRoot)
    {
        var indexPath = Path.Combine(extractedRoot, "index.json");
        var index = JsonSerializer.Deserialize(File.ReadAllText(indexPath), PodishJsonContext.Default.OciIndex)
                    ?? throw new InvalidOperationException("invalid OCI index.json");
        if (index.Manifests == null || index.Manifests.Count == 0)
            throw new InvalidOperationException("OCI archive has no manifests");

        var loaded = new List<string>();
        foreach (var manifestDesc in index.Manifests)
        {
            var refName = manifestDesc.Annotations != null &&
                          manifestDesc.Annotations.TryGetValue("org.opencontainers.image.ref.name", out var n)
                ? n
                : $"imported:{DigestHex(manifestDesc.Digest)[..12]}";

            var manifestBlobPath = OciBlobPath(extractedRoot, manifestDesc.Digest);
            var manifest =
                JsonSerializer.Deserialize(File.ReadAllText(manifestBlobPath), PodishJsonContext.Default.OciManifest)
                ?? throw new InvalidOperationException($"invalid manifest blob {manifestDesc.Digest}");

            var safeName = ToSafeImageName(refName);
            var storeDir = Path.Combine(_ociImagesDir, safeName);
            var blobsDir = Path.Combine(storeDir, "blobs");
            var indexesDir = Path.Combine(storeDir, "indexes");
            Directory.CreateDirectory(blobsDir);
            Directory.CreateDirectory(indexesDir);

            var layers = new List<OciStoredLayer>();
            foreach (var layerDesc in manifest.Layers)
            {
                var srcLayerBlob = OciBlobPath(extractedRoot, layerDesc.Digest);
                var tarPath = EnsureUncompressedTar(srcLayerBlob);
                try
                {
                    var tarHex = Sha256HexOfFile(tarPath);
                    var tarDigest = "sha256:" + tarHex;
                    var dstBlob = Path.Combine(blobsDir, $"{tarHex}.tar");
                    File.Copy(tarPath, dstBlob, true);

                    var indexFile = Path.Combine(indexesDir, $"{tarHex}.json");
                    using (var tarStream = File.OpenRead(dstBlob))
                    {
                        var layerIndex = OciLayerIndexBuilder.BuildFromTar(tarStream, tarDigest);
                        var persistedEntries = layerIndex.Entries.Values
                            .Select(e => e with { InlineData = null })
                            .ToList();
                        File.WriteAllText(indexFile,
                            JsonSerializer.Serialize(persistedEntries, PodishJsonContext.Default.ListLayerIndexEntry));
                    }

                    layers.Add(new OciStoredLayer(
                        tarDigest,
                        "application/vnd.oci.image.layer.v1.tar",
                        new FileInfo(dstBlob).Length,
                        OciStorePath.ToStoredPath(storeDir, dstBlob),
                        OciStorePath.ToStoredPath(storeDir, indexFile)));
                }
                finally
                {
                    if (tarPath != srcLayerBlob && File.Exists(tarPath))
                        try
                        {
                            File.Delete(tarPath);
                        }
                        catch
                        {
                        }
                }
            }

            var (registry, repository, tag) = ParseImageReference(refName);
            var stored = new OciStoredImage(
                refName,
                registry,
                repository,
                tag,
                manifestDesc.Digest,
                OciStorePath.RelativeStoreDirectory,
                layers);
            File.WriteAllText(Path.Combine(storeDir, "image.json"),
                JsonSerializer.Serialize(stored, PodishJsonContext.Default.OciStoredImage));
            loaded.Add(refName);
        }

        return loaded;
    }

    public string Import(string sourceTar, string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
            imageReference = "localhost/fiberpod-import:latest";

        var safeName = ToSafeImageName(imageReference);
        var storeDir = Path.Combine(_ociImagesDir, safeName);
        var blobsDir = Path.Combine(storeDir, "blobs");
        var indexesDir = Path.Combine(storeDir, "indexes");
        Directory.CreateDirectory(blobsDir);
        Directory.CreateDirectory(indexesDir);

        var tempFiles = new List<string>();
        try
        {
            var normalizedTar = PrepareImportTar(sourceTar, tempFiles);

            var digestHex = Sha256HexOfFile(normalizedTar);
            var digest = "sha256:" + digestHex;
            var blobPath = Path.Combine(blobsDir, $"{digestHex}.tar");
            File.Copy(normalizedTar, blobPath, true);

            LayerIndex index;
            using (var tarStream = File.OpenRead(blobPath))
            {
                index = OciLayerIndexBuilder.BuildFromTar(tarStream, digest);
            }

            var indexPath = Path.Combine(indexesDir, $"{digestHex}.json");
            File.WriteAllText(indexPath,
                JsonSerializer.Serialize(index.Entries.Values.ToList(), PodishJsonContext.Default.ListLayerIndexEntry));

            var (registry, repository, tag) = ParseImageReference(imageReference);
            var layerSize = new FileInfo(blobPath).Length;
            var image = new OciStoredImage(
                imageReference,
                registry,
                repository,
                tag,
                digest,
                OciStorePath.RelativeStoreDirectory,
                new[]
                {
                    new OciStoredLayer(
                        digest,
                        "application/vnd.oci.image.layer.v1.tar",
                        layerSize,
                        OciStorePath.ToStoredPath(storeDir, blobPath),
                        OciStorePath.ToStoredPath(storeDir, indexPath))
                });

            File.WriteAllText(Path.Combine(storeDir, "image.json"),
                JsonSerializer.Serialize(image, PodishJsonContext.Default.OciStoredImage));
            return image.ImageReference;
        }
        finally
        {
            foreach (var file in tempFiles)
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
        }
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

        using (var fs = File.Create(tmp))
        {
            ExportInternal(containerId, imageRef, upperStore, fs);
        }

        File.Move(tmp, outputArchive, true);
    }

    public void ExportStoredImage(string imageReferenceOrPath, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("output directory is required");

        var (storeDir, _) = ResolveStoreDirForImage(imageReferenceOrPath);
        var imagePath = Path.Combine(storeDir, "image.json");
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"image metadata not found: {imagePath}");

        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, true);
        Directory.CreateDirectory(outputDirectory);

        File.Copy(imagePath, Path.Combine(outputDirectory, "image.json"), true);
        CopyDirectory(Path.Combine(storeDir, "indexes"), Path.Combine(outputDirectory, "indexes"));
        CopyDirectory(Path.Combine(storeDir, "blobs"), Path.Combine(outputDirectory, "blobs"));
    }

    public void ExportToStream(string containerId, Stream output)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            throw new InvalidOperationException("container id is required");
        if (output == null || !output.CanWrite)
            throw new InvalidOperationException("output stream is not writable");

        var imageRef = ResolveImageReferenceForContainer(containerId);
        if (string.IsNullOrWhiteSpace(imageRef))
            throw new InvalidOperationException($"container not found: {containerId}");
        var containerDir = Path.Combine(_fiberpodDir, "containers", containerId);
        var upperStore = Path.Combine(containerDir, "silk-upper");
        ExportInternal(containerId, imageRef, upperStore, output);
    }

    private void ExportInternal(string containerId, string imageRef, string upperStore, Stream output)
    {
        _ = containerId;
        var (rootDentry, exportMount, disposer) = OpenExportView(imageRef, upperStore);
        try
        {
            using var writer = new TarWriter(output, TarEntryFormat.Pax, true);
            WriteDentryTreeToTar(writer, rootDentry, exportMount, "");
        }
        finally
        {
            disposer.Dispose();
        }
    }

    private (Dentry Root, Mount ExportMount, IDisposable Disposer) OpenExportView(string imageRef, string upperStore)
    {
        var devNumbers = new DeviceNumberManager();
        var (storeDir, _) = ResolveStoreDirForImage(imageRef);
        var imagePath = Path.Combine(storeDir, "image.json");
        if (!File.Exists(imagePath))
            throw new InvalidOperationException($"container image metadata missing: {imagePath}");
        var image = JsonSerializer.Deserialize(File.ReadAllText(imagePath), PodishJsonContext.Default.OciStoredImage)
                    ?? throw new InvalidOperationException($"invalid image metadata: {imagePath}");

        var (lowerSb, lowerProvider) = BuildLowerSuperBlock(image, storeDir, devNumbers);

        if (!Directory.Exists(upperStore))
        {
            var mount = new Mount(lowerSb, lowerSb.Root);
            return (lowerSb.Root, mount, new CompositeDisposable(lowerProvider));
        }

        var silkType = FileSystemRegistry.Get("silkfs")
                       ?? throw new InvalidOperationException("silkfs is not registered");
        var overlayType = FileSystemRegistry.Get("overlay")
                          ?? throw new InvalidOperationException("overlay is not registered");

        var upperSb = silkType.CreateFileSystem(devNumbers).ReadSuper(silkType, 0, upperStore, null);
        var overlaySb = overlayType.CreateFileSystem(devNumbers).ReadSuper(overlayType, 0, "export-overlay",
            new OverlayMountOptions
            {
                Lower = lowerSb,
                Upper = upperSb
            });
        var mountOverlay = new Mount(overlaySb, overlaySb.Root);
        return (overlaySb.Root, mountOverlay, new CompositeDisposable(lowerProvider));
    }

    private (SuperBlock Lower, IDisposable Provider) BuildLowerSuperBlock(OciStoredImage image, string storeDir,
        DeviceNumberManager devNumbers)
    {
        var layerIndexes = new List<IReadOnlyList<LayerIndexEntry>>(image.Layers.Count);
        var digestToBlobPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in image.Layers)
        {
            string indexPath;
            string blobPath;
            try
            {
                indexPath = OciStorePath.Resolve(storeDir, layer.IndexPath);
                blobPath = OciStorePath.Resolve(storeDir, layer.BlobPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"invalid layer stored path for digest {layer.Digest}: blob='{layer.BlobPath}', index='{layer.IndexPath}', store='{storeDir}', error='{ex.Message}'");
            }

            if (!File.Exists(indexPath))
                throw new InvalidOperationException(
                    $"missing layer index file: stored='{layer.IndexPath}', resolved='{indexPath}'");
            if (!File.Exists(blobPath))
                throw new InvalidOperationException(
                    $"missing layer blob file: stored='{layer.BlobPath}', resolved='{blobPath}'");
            var entries = JsonSerializer.Deserialize(File.ReadAllText(indexPath),
                              PodishJsonContext.Default.ListLayerIndexEntry)
                          ?? throw new InvalidOperationException($"invalid layer index JSON: {indexPath}");
            layerIndexes.Add(entries);
            digestToBlobPath[layer.Digest] = blobPath;
        }

        var merged = OciLayerIndexMerger.Merge(layerIndexes);
        var layerType = FileSystemRegistry.Get("layerfs");
        if (layerType == null)
            throw new InvalidOperationException("layerfs is not registered");
        var provider = new TarBlobLayerContentProvider(digestToBlobPath);
        var lowerSb = layerType.CreateFileSystem(devNumbers).ReadSuper(layerType, 0, "layer-lower",
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
                if (inode.Readlink(out byte[]? linkTargetBytes) < 0 || linkTargetBytes is not { Length: > 0 })
                    return;
                var linkTarget = FsEncoding.DecodeUtf8Strict(linkTargetBytes);
                var link = new PaxTarEntry(TarEntryType.SymbolicLink, relPath)
                {
                    LinkName = linkTarget
                };
                writer.WriteEntry(link);
                return;
            }
            else if (inode.Type == InodeType.File)
            {
                var bytes = ReadAllInodeBytes(root, mount);
                var file = new PaxTarEntry(TarEntryType.RegularFile, relPath)
                {
                    DataStream = new MemoryStream(bytes, false)
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
            .OrderBy(e => e.Name, FsName.BytewiseComparer)
            .ToList();
        foreach (var child in children)
        {
            var dentry = inode.Lookup(child.Name);
            if (dentry == null || dentry.Inode == null)
                continue;
            var childName = child.Name.ToString();
            var childRel = string.IsNullOrEmpty(relPath) ? childName : relPath + "/" + childName;
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
                var n = inode.ReadToHost(null, lf, buf, offset);
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
                evt = JsonSerializer.Deserialize(line, PodishJsonContext.Default.ContainerEvent);
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
                    {
                        entry.DataStream.CopyTo(outStream);
                    }

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

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string OciBlobPath(string extractedRoot, string digest)
    {
        var hex = DigestHex(digest);
        var p = Path.Combine(extractedRoot, "blobs", "sha256", hex);
        if (!File.Exists(p))
            throw new FileNotFoundException($"missing OCI blob {digest}");
        return p;
    }

    private static string EnsureUncompressedTar(string blobPath)
    {
        using var fs = File.OpenRead(blobPath);
        Span<byte> magic = stackalloc byte[2];
        if (fs.Read(magic) == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            fs.Position = 0;
            var tmpTar = Path.Combine(Path.GetTempPath(), $"fiberpod-layer-{Guid.NewGuid():N}.tar");
            using var gzip = new GZipStream(fs, CompressionMode.Decompress, true);
            using var outFile = File.Create(tmpTar);
            gzip.CopyTo(outFile);
            return tmpTar;
        }

        return blobPath;
    }

    private static string PrepareImportTar(string source, List<string> tempFiles)
    {
        var localPath = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            localPath = DownloadToTempFile(uri);
            tempFiles.Add(localPath);
        }

        if (!File.Exists(localPath))
            throw new FileNotFoundException($"import source not found: {source}");

        var tarPath = EnsureUncompressedTar(localPath);
        if (tarPath != localPath)
            tempFiles.Add(tarPath);

        ValidateTarHeader(tarPath);
        return tarPath;
    }

    private static string DownloadToTempFile(Uri uri)
    {
        using var http = new HttpClient();
        using var resp = http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        var tmp = Path.Combine(Path.GetTempPath(), $"fiberpod-import-{Guid.NewGuid():N}");
        using var input = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var outFile = File.Create(tmp);
        input.CopyTo(outFile);
        return tmp;
    }

    private static void ValidateTarHeader(string tarPath)
    {
        using var fs = File.OpenRead(tarPath);
        if (fs.Length < 512)
            throw new InvalidOperationException("import source is not a valid tar archive");
        var header = new byte[512];
        var n = fs.Read(header, 0, header.Length);
        if (n < 512)
            throw new InvalidOperationException("import source is not a valid tar archive");
        var allZero = header.All(b => b == 0);
        if (allZero)
            throw new InvalidOperationException("import source tar archive is empty");
        var magic = Encoding.ASCII.GetString(header, 257, 5);
        if (magic != "ustar")
            throw new InvalidOperationException(
                "unsupported import archive format: expected tar or tar.gz");
    }

    private static void CreateTarFromDirectory(string sourceDir, string tarPath)
    {
        using var fs = File.Create(tarPath);
        using var writer = new TarWriter(fs, TarEntryFormat.Pax);
        foreach (var path in Directory.EnumerateFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(sourceDir, path).Replace('\\', '/');
            if (Directory.Exists(path))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rel.TrimEnd('/') + "/"));
            else if (File.Exists(path)) writer.WriteEntry(path, rel);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"directory not found: {sourceDir}");

        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(destinationDir, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
                Directory.CreateDirectory(targetDirectory);
            File.Copy(file, targetPath, true);
        }
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

    private static void EnsureFileSystemsRegistered()
    {
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "hostfs", Factory = static devMgr => new Hostfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "tmpfs", Factory = static devMgr => new Tmpfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "devtmpfs", Factory = static devMgr => new Tmpfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "proc", Factory = static devMgr => new ProcFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "overlay", Factory = static devMgr => new OverlayFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "layerfs", Factory = static devMgr => new LayerFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "silkfs", Factory = static devMgr => new SilkFileSystem(devMgr) });
    }
}

internal sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
{
    private readonly IDisposable[] _disposables = disposables;

    public void Dispose()
    {
        foreach (var d in _disposables)
            try
            {
                d.Dispose();
            }
            catch
            {
            }
    }
}

internal sealed class TarBlobLayerContentProvider : ILayerContentProvider, IDisposable
{
    private readonly Dictionary<string, string> _digestToBlobPath;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileStream> _streams = new(StringComparer.Ordinal);

    public TarBlobLayerContentProvider(Dictionary<string, string> digestToBlobPath)
    {
        _digestToBlobPath = digestToBlobPath;
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

    public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (entry.Type != InodeType.File) return true;

        var hasBlobBacking = entry.DataOffset >= 0 && !string.IsNullOrWhiteSpace(entry.BlobDigest);
        if (!hasBlobBacking && entry.InlineData != null)
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
}

internal sealed record OciLayout(
    [property: JsonPropertyName("imageLayoutVersion")]
    string ImageLayoutVersion);

internal sealed record OciIndex(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("manifests")]
    List<OciDescriptor> Manifests);

internal sealed record OciDescriptor(
    [property: JsonPropertyName("mediaType")]
    string MediaType,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("annotations")]
    Dictionary<string, string>? Annotations = null);

internal sealed record OciManifest(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("mediaType")]
    string MediaType,
    [property: JsonPropertyName("config")] OciDescriptor Config,
    [property: JsonPropertyName("layers")] List<OciDescriptor> Layers);

internal sealed record OciImageConfig(
    [property: JsonPropertyName("architecture")]
    string Architecture,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("rootfs")] OciRootFs Rootfs,
    [property: JsonPropertyName("history")]
    List<OciHistory> History);

internal sealed record OciRootFs(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("diff_ids")]
    List<string> DiffIds);

internal sealed record OciHistory(
    [property: JsonPropertyName("created_by")]
    string CreatedBy);
