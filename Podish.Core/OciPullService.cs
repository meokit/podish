using System.Formats.Tar;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Podish.Core;

public class OciPullService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient _httpClient = new();
    private const string PullProgressMarker = "PODISH_IMAGE_PULL ";
    private static readonly TimeSpan[] NetworkPermissionRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3)
    ];
    private readonly ILogger _logger;

    public OciPullService(ILogger logger)
    {
        _logger = logger;
    }

    private sealed class ProgressReportingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long, bool> _onProgress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly long _reportStepBytes;
        private long _lastReportedBytes;
        private long _reportedBytes;
        private bool _completed;

        public ProgressReportingReadStream(Stream inner, long reportStepBytes, Action<long, bool> onProgress)
        {
            _inner = inner;
            _onProgress = onProgress;
            _reportStepBytes = Math.Max(32 * 1024, reportStepBytes);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.CanSeek ? _inner.Length : _reportedBytes;

        public override long Position
        {
            get => _inner.CanSeek ? _inner.Position : _reportedBytes;
            set
            {
                if (!_inner.CanSeek)
                    throw new NotSupportedException();
                _inner.Position = value;
            }
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            ReportProgress(bytesRead);
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _inner.Read(buffer);
            ReportProgress(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
            ReportProgress(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            ReportProgress(bytesRead);
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                ReportCompletion();
            base.Dispose(disposing);
        }

        private void ReportProgress(int bytesRead)
        {
            if (bytesRead > 0)
                _reportedBytes += bytesRead;

            if (bytesRead <= 0)
            {
                ReportCompletion();
                return;
            }

            if (_reportedBytes - _lastReportedBytes < _reportStepBytes &&
                _stopwatch.ElapsedMilliseconds < 150)
                return;

            _lastReportedBytes = _reportedBytes;
            _stopwatch.Restart();
            _onProgress(_reportedBytes, false);
        }

        private void ReportCompletion()
        {
            if (_completed)
                return;

            _completed = true;
            _lastReportedBytes = _reportedBytes;
            _onProgress(_reportedBytes, true);
        }
    }

    public async Task PullAndExtractImageAsync(string imageReference, string outputDirectory)
    {
        await ExecuteWithInitialNetworkRetryAsync(imageReference, async () =>
        {
            // Parse image reference: registry/repository:tag
            // E.g. docker.io/library/alpine:latest or ghcr.io/namespace/image:tag
            var parsed = ParseImageReference(imageReference);
            var registry = parsed.Registry;
            var repository = parsed.Repository;
            var tag = parsed.Tag;

            _logger.LogInformation("Pulling {Registry}/{Repository}:{Tag} into {OutputDir}...", registry, repository,
                tag,
                outputDirectory);

            try
            {
                // 1. Get Auth Token
                var token = await GetAuthTokenAsync(registry, repository);
                if (token != null)
                    _logger.LogDebug("Successfully obtained auth token.");
                else
                    _logger.LogDebug("No auth token needed, or anonymous access granted immediately.");

                // 2. Get Manifest
                var manifestStr = await GetManifestAsync(registry, repository, tag, token);
                var manifestDoc = JsonDocument.Parse(manifestStr);

                var schemaVersion = manifestDoc.RootElement.GetProperty("schemaVersion").GetInt32();
                if (schemaVersion != 2) throw new NotSupportedException($"Unsupported schema version: {schemaVersion}");

                var (layers, _, _) = await ResolveImageLayersAsync(registry, repository, token, manifestDoc,
                    manifestStr);

                _logger.LogInformation("Found {Count} layers to download.", layers.Count);

                // 3. Download and Extract each layer (sequentially for simplicity, or in parallel)
                Directory.CreateDirectory(outputDirectory);

                foreach (var layer in layers)
                {
                    var digest = layer.Digest;
                    var mediaType = layer.MediaType;
                    var size = layer.Size;

                    _logger.LogInformation("Downloading layer {Digest} ({Size} bytes) type {MediaType}...",
                        digest[..15] + "...", size, mediaType);
                    await DownloadAndExtractLayerAsync(registry, repository, digest, token, outputDirectory);
                }

                _logger.LogInformation(
                    "Successfully pulled and extracted {Registry}/{Repository}:{Tag} to {OutputDir}",
                    registry, repository, tag, outputDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull image {ImageReference}: {Message}", imageReference, ex.Message);
                throw;
            }
        });
    }

    public async Task<OciStoredImage> PullAndStoreImageAsync(string imageReference, string storeDirectory)
    {
        return await ExecuteWithInitialNetworkRetryAsync(imageReference, async () =>
        {
            var storeName =
                Path.GetFileName(storeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(storeName))
                storeName = "oci-store";
            var lockDir = Path.GetDirectoryName(storeDirectory) ?? storeDirectory;
            var lockPath = Path.Combine(lockDir, $".{storeName}.pull.lock");
            using var storeLock = CooperativeFileLock.Acquire(lockPath, TimeSpan.FromMinutes(10));

            var parsed = ParseImageReference(imageReference);
            var registry = parsed.Registry;
            var repository = parsed.Repository;
            var tag = parsed.Tag;

            _logger.LogInformation("Pulling {Registry}/{Repository}:{Tag} into OCI store {StoreDir}...", registry,
                repository,
                tag, storeDirectory);
            EmitPullProgress(imageReference, "resolving", $"Resolving image {imageReference}");

            try
            {
                Directory.CreateDirectory(storeDirectory);
                var blobsDir = Path.Combine(storeDirectory, "blobs", "sha256");
                var indexesDir = Path.Combine(storeDirectory, "indexes");
                Directory.CreateDirectory(blobsDir);
                Directory.CreateDirectory(indexesDir);

                var token = await GetAuthTokenAsync(registry, repository);
                var manifestStr = await GetManifestAsync(registry, repository, tag, token);
                using var manifestDoc = JsonDocument.Parse(manifestStr);
                var (layers, manifestDigest, runtimeConfig) =
                    await ResolveImageLayersAsync(registry, repository, token, manifestDoc, manifestStr);
                var overallTotalBytes = layers.Sum(layer => Math.Max(0, layer.Size));
                var overallCompletedBytes = 0L;
                EmitPullProgress(imageReference, "downloading",
                    $"Found {layers.Count} layer{(layers.Count == 1 ? string.Empty : "s")} to download",
                    overallCompletedBytes, overallTotalBytes, 0, overallTotalBytes, 0, layers.Count);

                var storedLayers = new List<OciStoredLayer>(layers.Count);
                for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                {
                    var layer = layers[layerIndex];
                    var digestHex = DigestHex(layer.Digest);
                    var tarBlobPath = Path.Combine(blobsDir, $"{digestHex}.tar");
                    var indexPath = Path.Combine(indexesDir, $"{digestHex}.json");
                    var humanLayerIndex = layerIndex + 1;

                    if (!File.Exists(tarBlobPath))
                    {
                        EmitPullProgress(imageReference, "downloading",
                            $"Downloading layer {humanLayerIndex}/{layers.Count}",
                            overallCompletedBytes, overallTotalBytes, 0, layer.Size, humanLayerIndex, layers.Count);
                        await DownloadAndExpandLayerAsTarAsync(
                            registry,
                            repository,
                            layer.Digest,
                            token,
                            tarBlobPath,
                            imageReference,
                            humanLayerIndex,
                            layers.Count,
                            overallCompletedBytes,
                            overallTotalBytes,
                            layer.Size);
                    }
                    else
                    {
                        EmitPullProgress(imageReference, "extracting",
                            $"Reusing cached layer {humanLayerIndex}/{layers.Count}",
                            overallCompletedBytes + layer.Size,
                            overallTotalBytes,
                            layer.Size,
                            layer.Size,
                            humanLayerIndex,
                            layers.Count);
                    }

                    if (!File.Exists(indexPath))
                    {
                        EmitPullProgress(imageReference, "extracting",
                            $"Indexing layer {humanLayerIndex}/{layers.Count}",
                            overallCompletedBytes + layer.Size,
                            overallTotalBytes,
                            layer.Size,
                            layer.Size,
                            humanLayerIndex,
                            layers.Count);
                        using var tarStream = File.OpenRead(tarBlobPath);
                        var index = OciLayerIndexBuilder.BuildFromTar(tarStream, layer.Digest);
                        var persistedEntries = index.Entries.Values
                            .Select(e => e with { InlineData = null })
                            .ToList();
                        await File.WriteAllTextAsync(indexPath,
                            JsonSerializer.Serialize(persistedEntries, PodishJsonContext.Default.ListLayerIndexEntry));
                    }

                    storedLayers.Add(new OciStoredLayer(
                        layer.Digest,
                        layer.MediaType,
                        layer.Size,
                        OciStorePath.ToStoredPath(storeDirectory, tarBlobPath),
                        OciStorePath.ToStoredPath(storeDirectory, indexPath)));
                    overallCompletedBytes += layer.Size;
                    EmitPullProgress(imageReference, "extracting",
                        $"Prepared layer {humanLayerIndex}/{layers.Count}",
                        overallCompletedBytes,
                        overallTotalBytes,
                        layer.Size,
                        layer.Size,
                        humanLayerIndex,
                        layers.Count);
                }

                var image = new OciStoredImage(
                    imageReference,
                    registry,
                    repository,
                    tag,
                    manifestDigest,
                    OciStorePath.RelativeStoreDirectory,
                    storedLayers,
                    runtimeConfig?.User,
                    runtimeConfig?.Entrypoint,
                    runtimeConfig?.Cmd,
                    runtimeConfig?.Env,
                    runtimeConfig?.WorkingDir);
                await File.WriteAllTextAsync(Path.Combine(storeDirectory, "image.json"),
                    JsonSerializer.Serialize(image, PodishJsonContext.Default.OciStoredImage));

                _logger.LogInformation("Stored image {ImageReference} in OCI store {StoreDir} with {LayerCount} layers",
                    imageReference, storeDirectory, storedLayers.Count);
                EmitPullProgress(imageReference, "completed", $"Finished pulling {imageReference}",
                    overallTotalBytes, overallTotalBytes, null, null, layers.Count, layers.Count);
                return image;
            }
            catch (Exception ex)
            {
                EmitPullProgress(imageReference, "failed", ex.Message);
                throw;
            }
        });
    }

    private async Task<T> ExecuteWithInitialNetworkRetryAsync<T>(string imageReference, Func<Task<T>> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < NetworkPermissionRetryDelays.Length && IsRetryableInitialNetworkFailure(ex))
            {
                var delay = NetworkPermissionRetryDelays[attempt];
                _logger.LogWarning(ex,
                    "Initial network access failed while pulling {ImageReference}; retrying in {DelaySeconds}s",
                    imageReference, delay.TotalSeconds);
                EmitPullProgress(
                    imageReference,
                    "resolving",
                    $"Waiting for network access... If macOS shows a permission dialog, allow it to continue. Retrying in {delay.Seconds}s."
                );
                await Task.Delay(delay);
            }
        }
    }

    private async Task ExecuteWithInitialNetworkRetryAsync(string imageReference, Func<Task> action)
    {
        await ExecuteWithInitialNetworkRetryAsync(imageReference, async () =>
        {
            await action();
            return true;
        });
    }

    private static bool IsRetryableInitialNetworkFailure(Exception ex)
    {
        return ex switch
        {
            OperationCanceledException => false,
            HttpRequestException httpEx => httpEx.StatusCode == null || (int)httpEx.StatusCode >= 500,
            SocketException => true,
            _ when ex.InnerException != null => IsRetryableInitialNetworkFailure(ex.InnerException),
            _ => false
        };
    }

    private async Task<string?> GetAuthTokenAsync(string registry, string repository)
    {
        // 1. First make an anonymous request to see if we get a 401 with Www-Authenticate
        var pingUrl = $"https://{registry}/v2/";
        var pingResponse = await _httpClient.GetAsync(pingUrl);

        if (pingResponse.IsSuccessStatusCode)
            // No auth required
            return null;

        if (pingResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            var wwwAuth = pingResponse.Headers.WwwAuthenticate.FirstOrDefault()?.Parameter;
            if (wwwAuth != null)
            {
                // Parse standard Bearer realm="https://auth.docker.io/token",service="registry.docker.io"
                var parts = wwwAuth.Split(',');
                string realm = "", service = "";

                foreach (var part in parts)
                {
                    var kv = part.Trim().Split('=');
                    if (kv.Length == 2)
                    {
                        var key = kv[0].Trim();
                        var value = kv[1].Trim().Trim('"');

                        if (key.Equals("realm", StringComparison.OrdinalIgnoreCase)) realm = value;
                        if (key.Equals("service", StringComparison.OrdinalIgnoreCase)) service = value;
                    }
                }

                if (!string.IsNullOrEmpty(realm))
                {
                    // Docker Hub uses `scope=repository:library/alpine:pull` format
                    var authUrl = $"{realm}?service={service}&scope=repository:{repository}:pull";

                    var response = await _httpClient.GetAsync(authUrl);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("token").GetString();
                }
            }
        }

        return null;
    }

    private async Task<string> GetManifestAsync(string registry, string repository, string tag, string? token)
    {
        var manifestUrl = $"https://{registry}/v2/{repository}/manifests/{tag}";
        var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Request OCI or Docker V2 manifest and manifest list
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.list.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.index.v1+json"));

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<OciStoredImageRuntimeConfig?> VerifyArchitectureAndReadRuntimeConfigAsync(string registry, string repository,
        string digest, string? token)
    {
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/{digest}";
        var request = new HttpRequestMessage(HttpMethod.Get, blobUrl);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var configJson = await response.Content.ReadAsStringAsync();
        var config = JsonSerializer.Deserialize(configJson, PodishJsonContext.Default.OciImageConfig);

        var arch = config?.Architecture;
        if (arch != null && arch != "386")
            throw new NotSupportedException(
                $"Image architecture is '{arch}', but Podish.Cli emulator requires '386' (32-bit x86).");

        if (config?.Config == null)
            return null;

        return new OciStoredImageRuntimeConfig(
            string.IsNullOrWhiteSpace(config.Config.User) ? null : config.Config.User,
            config.Config.Entrypoint,
            config.Config.Cmd,
            config.Config.Env,
            string.IsNullOrWhiteSpace(config.Config.WorkingDir) ? null : config.Config.WorkingDir);
    }

    private async Task DownloadAndExtractLayerAsync(string registry, string repository, string digest, string? token,
        string outputDirectory)
    {
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/{digest}";
        var request = new HttpRequestMessage(HttpMethod.Get, blobUrl);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync();

        // Docker layers are gzipped tarballs
        using var gzipStream = new GZipStream(contentStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (await tarReader.GetNextEntryAsync() is { } entry)
        {
            var entryName = entry.Name.TrimStart('/', '\\');
            var targetPath = Path.Combine(outputDirectory, entryName);
            var dir = Path.GetDirectoryName(targetPath);

            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(targetPath);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    entry.ExtractToFile(targetPath, true);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    try
                    {
                        // Use raw link name even if it's absolute, as it refers to a path inside the emulator
                        File.CreateSymbolicLink(targetPath, entry.LinkName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to create symlink {Link} -> {Target}: {Msg}", targetPath,
                            entry.LinkName, ex.Message);
                    }

                    break;
                default:
                    // Soft skip other types like BlockDevice, CharacterDevice, etc for local extraction
                    _logger.LogDebug("Skipping unhandled tar entry type: {Type} for {Name}", entry.EntryType,
                        entry.Name);
                    break;
            }
        }
    }

    private async Task DownloadAndExpandLayerAsTarAsync(string registry, string repository, string digest,
        string? token,
        string outputTarPath,
        string imageReference,
        int layerIndex,
        int layerCount,
        long overallCompletedBytesBeforeLayer,
        long overallTotalBytes,
        long layerTotalBytes)
    {
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/{digest}";
        var request = new HttpRequestMessage(HttpMethod.Get, blobUrl);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var reportingStream = new ProgressReportingReadStream(
            contentStream,
            Math.Max(128 * 1024, layerTotalBytes / 40),
            (layerBytes, completed) =>
            {
                var clampedLayerBytes = Math.Min(layerTotalBytes, Math.Max(0, layerBytes));
                EmitPullProgress(imageReference, completed ? "extracting" : "downloading",
                    completed
                        ? $"Downloaded layer {layerIndex}/{layerCount}"
                        : $"Downloading layer {layerIndex}/{layerCount}",
                    Math.Min(overallTotalBytes, overallCompletedBytesBeforeLayer + clampedLayerBytes),
                    overallTotalBytes,
                    clampedLayerBytes,
                    layerTotalBytes,
                    layerIndex,
                    layerCount);
            });
        await using var gzipStream = new GZipStream(reportingStream, CompressionMode.Decompress);
        await using var file = File.Open(outputTarPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await gzipStream.CopyToAsync(file);
    }

    private void EmitPullProgress(
        string imageReference,
        string state,
        string message,
        long? overallBytes = null,
        long? overallTotalBytes = null,
        long? layerBytes = null,
        long? layerTotalBytes = null,
        int? layerIndex = null,
        int? layerCount = null)
    {
        var payload = new ImagePullProgressMessage(
            imageReference,
            state,
            message,
            overallBytes,
            overallTotalBytes,
            layerBytes,
            layerTotalBytes,
            layerIndex,
            layerCount);
        _logger.LogInformation("{Payload}",
            PullProgressMarker + JsonSerializer.Serialize(payload, PodishJsonContext.Default.ImagePullProgressMessage));
    }

    private async Task<(List<LayerDescriptor> Layers, string ManifestDigest, OciStoredImageRuntimeConfig? RuntimeConfig)> ResolveImageLayersAsync(
        string registry,
        string repository,
        string? token,
        JsonDocument manifestDoc,
        string manifestStr)
    {
        if (manifestDoc.RootElement.GetProperty("schemaVersion").GetInt32() != 2)
            throw new NotSupportedException("Only OCI/Docker schema v2 is supported");

        var manifest = manifestDoc.RootElement;
        var manifestDigest = Sha256Digest(manifestStr);

        if (manifest.TryGetProperty("manifests", out var manifestsArray))
        {
            _logger.LogInformation("Received a manifest list. Looking for architecture '386'...");
            string? targetDigest = null;
            foreach (var m in manifestsArray.EnumerateArray())
                if (m.TryGetProperty("platform", out var platform) &&
                    platform.TryGetProperty("architecture", out var arch) &&
                    arch.GetString() == "386")
                {
                    targetDigest = m.GetProperty("digest").GetString();
                    break;
                }

            if (targetDigest == null)
                throw new NotSupportedException(
                    "This image does not provide a 32-bit x86 (386) architecture manifest.");

            _logger.LogInformation("Found 386 manifest at digest {Digest}. Fetching it...", targetDigest);
            var resolvedManifest = await GetManifestAsync(registry, repository, targetDigest, token);
            using var resolvedDoc = JsonDocument.Parse(resolvedManifest);
            manifest = resolvedDoc.RootElement;
            manifestDigest = targetDigest;
            OciStoredImageRuntimeConfig? runtimeConfig = null;
            var resolvedConfigDigest = manifest.GetProperty("config").GetProperty("digest").GetString();
            if (!string.IsNullOrEmpty(resolvedConfigDigest))
                runtimeConfig = await VerifyArchitectureAndReadRuntimeConfigAsync(registry, repository, resolvedConfigDigest,
                    token);

            var resolvedLayers = manifest
                .GetProperty("layers")
                .EnumerateArray()
                .Select(x => new LayerDescriptor(
                    x.GetProperty("digest").GetString()!,
                    x.GetProperty("mediaType").GetString()!,
                    x.GetProperty("size").GetInt64()))
                .ToList();
            return (resolvedLayers, manifestDigest, runtimeConfig);
        }

        var configDigest = manifest.GetProperty("config").GetProperty("digest").GetString();
        OciStoredImageRuntimeConfig? runtimeConfigFinal = null;
        if (!string.IsNullOrEmpty(configDigest))
            runtimeConfigFinal = await VerifyArchitectureAndReadRuntimeConfigAsync(registry, repository, configDigest, token);

        var layers = manifest
            .GetProperty("layers")
            .EnumerateArray()
            .Select(x => new LayerDescriptor(
                x.GetProperty("digest").GetString()!,
                x.GetProperty("mediaType").GetString()!,
                x.GetProperty("size").GetInt64()))
            .ToList();
        return (layers, manifestDigest, runtimeConfigFinal);
    }

    private static (string Registry, string Repository, string Tag) ParseImageReference(string imageReference)
    {
        var firstSlashIndex = imageReference.IndexOf('/');
        if (firstSlashIndex == -1 || !imageReference[..firstSlashIndex].Contains('.'))
            throw new ArgumentException(
                "Refusing to pull short name. You must specify a registry (e.g. docker.io/library/alpine:latest or ghcr.io/namespace/image)");

        var registry = imageReference[..firstSlashIndex];
        if (registry == "docker.io")
            registry = "registry-1.docker.io";

        var rest = imageReference[(firstSlashIndex + 1)..];
        var colonIndex = rest.IndexOf(':');
        if (colonIndex == -1)
            return (registry, rest, "latest");
        return (registry, rest[..colonIndex], rest[(colonIndex + 1)..]);
    }

    private static string DigestHex(string digest)
    {
        var parts = digest.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? parts[1] : digest;
    }

    private static string Sha256Digest(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private readonly record struct LayerDescriptor(string Digest, string MediaType, long Size);
}

internal sealed record ImagePullProgressMessage(
    string ImageReference,
    string State,
    string Message,
    long? OverallBytes = null,
    long? OverallTotalBytes = null,
    long? LayerBytes = null,
    long? LayerTotalBytes = null,
    int? LayerIndex = null,
    int? LayerCount = null);
