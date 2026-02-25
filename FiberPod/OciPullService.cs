using System.Net.Http.Headers;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace FiberPod;

public class OciPullService
{
    private static readonly HttpClient _httpClient = new();
    private readonly ILogger _logger;

    public OciPullService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task PullAndExtractImageAsync(string imageReference, string outputDirectory)
    {
        // Parse image reference: registry/repository:tag
        // E.g. docker.io/library/alpine:latest or ghcr.io/namespace/image:tag
        var firstSlashIndex = imageReference.IndexOf('/');
        if (firstSlashIndex == -1 || !imageReference[..firstSlashIndex].Contains('.'))
        {
            throw new ArgumentException("Refusing to pull short name. You must specify a registry (e.g. docker.io/library/alpine:latest or ghcr.io/namespace/image)");
        }

        var registry = imageReference[..firstSlashIndex];
        if (registry == "docker.io")
        {
            registry = "registry-1.docker.io";
        }
        var rest = imageReference[(firstSlashIndex + 1)..];

        var colonIndex = rest.IndexOf(':');
        string repository;
        string tag;

        if (colonIndex == -1)
        {
            repository = rest;
            tag = "latest";
        }
        else
        {
            repository = rest[..colonIndex];
            tag = rest[(colonIndex + 1)..];
        }

        _logger.LogInformation("Pulling {Registry}/{Repository}:{Tag} into {OutputDir}...", registry, repository, tag, outputDirectory);

        try
        {
            // 1. Get Auth Token
            var token = await GetAuthTokenAsync(registry, repository);
            if (token != null)
            {
                _logger.LogDebug("Successfully obtained auth token.");
            }
            else
            {
                _logger.LogDebug("No auth token needed, or anonymous access granted immediately.");
            }

            // 2. Get Manifest
            var manifestStr = await GetManifestAsync(registry, repository, tag, token);
            var manifestDoc = JsonDocument.Parse(manifestStr);

            var schemaVersion = manifestDoc.RootElement.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion != 2)
            {
                throw new NotSupportedException($"Unsupported schema version: {schemaVersion}");
            }

            JsonElement layers;
            
            // Handle Manifest List / OCI Index
            if (manifestDoc.RootElement.TryGetProperty("manifests", out var manifestsArray))
            {
                _logger.LogInformation("Received a manifest list. Looking for architecture '386'...");
                string? targetDigest = null;
                foreach (var m in manifestsArray.EnumerateArray())
                {
                    if (m.TryGetProperty("platform", out var platform) && 
                        platform.TryGetProperty("architecture", out var arch) && 
                        arch.GetString() == "386")
                    {
                        targetDigest = m.GetProperty("digest").GetString();
                        break;
                    }
                }

                if (targetDigest == null)
                {
                    throw new NotSupportedException("This image does not provide a 32-bit x86 (386) architecture manifest.");
                }

                _logger.LogInformation("Found 386 manifest at digest {Digest}. Fetching it...", targetDigest);
                manifestStr = await GetManifestAsync(registry, repository, targetDigest, token);
                manifestDoc = JsonDocument.Parse(manifestStr);
                layers = manifestDoc.RootElement.GetProperty("layers");
            }
            else
            {
                // Single manifest
                var configDigest = manifestDoc.RootElement.GetProperty("config").GetProperty("digest").GetString();
                if (!string.IsNullOrEmpty(configDigest))
                {
                    await VerifyArchitectureAsync(registry, repository, configDigest, token);
                }
                layers = manifestDoc.RootElement.GetProperty("layers");
            }

            _logger.LogInformation("Found {Count} layers to download.", layers.GetArrayLength());

            // 3. Download and Extract each layer (sequentially for simplicity, or in parallel)
            Directory.CreateDirectory(outputDirectory);

            foreach (var layer in layers.EnumerateArray())
            {
                var digest = layer.GetProperty("digest").GetString()!;
                var mediaType = layer.GetProperty("mediaType").GetString()!;
                var size = layer.GetProperty("size").GetInt64();

                _logger.LogInformation("Downloading layer {Digest} ({Size} bytes) type {MediaType}...", digest[..15] + "...", size, mediaType);
                await DownloadAndExtractLayerAsync(registry, repository, digest, token, outputDirectory);
            }

            _logger.LogInformation("Successfully pulled and extracted {Registry}/{Repository}:{Tag} to {OutputDir}", registry, repository, tag, outputDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull image {ImageReference}: {Message}", imageReference, ex.Message);
            throw;
        }
    }

    private async Task<string?> GetAuthTokenAsync(string registry, string repository)
    {
        // 1. First make an anonymous request to see if we get a 401 with Www-Authenticate
        var pingUrl = $"https://{registry}/v2/";
        var pingResponse = await _httpClient.GetAsync(pingUrl);

        if (pingResponse.IsSuccessStatusCode)
        {
            // No auth required
            return null;
        }

        if (pingResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
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
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        // Request OCI or Docker V2 manifest and manifest list
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.list.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.index.v1+json"));

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task VerifyArchitectureAsync(string registry, string repository, string digest, string? token)
    {
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/{digest}";
        var request = new HttpRequestMessage(HttpMethod.Get, blobUrl);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var configJson = await response.Content.ReadAsStringAsync();
        var configDoc = JsonDocument.Parse(configJson);
        
        if (configDoc.RootElement.TryGetProperty("architecture", out var archProp))
        {
            var arch = archProp.GetString();
            if (arch != null && arch != "386")
            {
                throw new NotSupportedException($"Image architecture is '{arch}', but FiberPod emulator requires '386' (32-bit x86).");
            }
        }
    }

    private async Task DownloadAndExtractLayerAsync(string registry, string repository, string digest, string? token, string outputDirectory)
    {
        var blobUrl = $"https://{registry}/v2/{repository}/blobs/{digest}";
        var request = new HttpRequestMessage(HttpMethod.Get, blobUrl);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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

            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(targetPath);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    entry.ExtractToFile(targetPath, overwrite: true);
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
                        _logger.LogWarning("Failed to create symlink {Link} -> {Target}: {Msg}", targetPath, entry.LinkName, ex.Message);
                    }
                    break;
                default:
                    // Soft skip other types like BlockDevice, CharacterDevice, etc for local extraction
                    _logger.LogDebug("Skipping unhandled tar entry type: {Type} for {Name}", entry.EntryType, entry.Name);
                    break;
            }
        }
    }
}
