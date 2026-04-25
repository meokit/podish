using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Runtime.Versioning;
using Fiberish.Core;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core;

namespace Podish.Browser;

[SupportedOSPlatform("browser")]
internal static class BrowserLayerRootfs
{
    private const int MinimumReadAheadBytes = 128 * 1024;

    public static Func<DeviceNumberManager, SuperBlock> CreateRootFileSystemFactory(string imageJsonUrl, int timeoutMs = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageJsonUrl);

        var imageUri = new Uri(imageJsonUrl, UriKind.Absolute);
        var storedImage = ReadJson(imageUri.ToString(), PodishJsonContext.Default.OciStoredImage, timeoutMs)
                          ?? throw new InvalidOperationException($"Failed to parse stored image metadata from '{imageJsonUrl}'.");
        if (storedImage.Layers.Count == 0)
            throw new InvalidOperationException($"Stored image '{imageJsonUrl}' has no layers.");

        var digestToBlobUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        var layerEntries = new List<IReadOnlyList<LayerIndexEntry>>(storedImage.Layers.Count);
        foreach (var layer in storedImage.Layers)
        {
            var indexUrl = new Uri(imageUri, layer.IndexPath).ToString();
            var blobUrl = new Uri(imageUri, layer.BlobPath).ToString();
            var entries = ReadJson(indexUrl, PodishJsonContext.Default.ListLayerIndexEntry, timeoutMs)
                          ?? throw new InvalidOperationException($"Failed to parse layer index '{indexUrl}'.");
            layerEntries.Add(entries);
            digestToBlobUrl[layer.Digest] = blobUrl;
        }

        var mergedIndex = OciLayerIndexMerger.Merge(layerEntries);
        return devNumbers =>
        {
            var lowerType = new FileSystemType { Name = "layerfs", Factory = static devMgr => new LayerFileSystem(devMgr) };
            var lowerSuper = new LayerFileSystem(devNumbers).ReadSuper(
                lowerType,
                0,
                imageJsonUrl,
                new LayerMountOptions
                {
                    Index = mergedIndex,
                    ContentProvider = new BrowserHttpLayerContentProvider(digestToBlobUrl, timeoutMs),
                    MinimumReadAheadBytes = MinimumReadAheadBytes
                });

            var upperType = new FileSystemType { Name = "tmpfs", Factory = static devMgr => new Tmpfs(devMgr) };
            var upperSuper = new Tmpfs(devNumbers).ReadSuper(upperType, 0, "browser-upper-tmpfs", null);

            var overlayType = new FileSystemType { Name = "overlay", Factory = static devMgr => new OverlayFileSystem(devMgr) };
            return new OverlayFileSystem(devNumbers).ReadSuper(
                overlayType,
                0,
                "browser-overlay-rootfs",
                new OverlayMountOptions
                {
                    Lower = lowerSuper,
                    Upper = upperSuper,
                    LowerRoots = [lowerSuper.Root!],
                    UpperRoot = upperSuper.Root
                });
        };
    }

    private static T? ReadJson<T>(string url, JsonTypeInfo<T> jsonTypeInfo, int timeoutMs)
    {
        var payload = BrowserHttpRpc.ReadAllBytes(url, timeoutMs);
        var jsonPayload = NormalizeJsonPayload(payload);
        BrowserExports.EmitDiagnosticLog(LogLevel.Information,
            $"[layer-rootfs] json-read url={url} rawLength={payload.Length} normalizedLength={jsonPayload.Length} rawTail={DescribeTailBytes(payload)} normalizedTail={DescribeTailBytes(jsonPayload)}");
        return JsonSerializer.Deserialize(jsonPayload, jsonTypeInfo);
    }

    private static ReadOnlySpan<byte> NormalizeJsonPayload(byte[] payload)
    {
        var start = 0;
        var end = payload.Length;

        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
            start = 3;

        while (start < end && IsIgnorableJsonBoundaryByte(payload[start]))
            start++;

        while (end > start && IsIgnorableJsonBoundaryByte(payload[end - 1]))
            end--;

        return payload.AsSpan(start, end - start);
    }

    private static bool IsIgnorableJsonBoundaryByte(byte value)
    {
        return value == 0 || value == (byte)' ' || value == (byte)'\t' || value == (byte)'\r' ||
               value == (byte)'\n';
    }

    private static string DescribeTailBytes(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return "<empty>";

        const int maxTailBytes = 16;
        var start = Math.Max(0, payload.Length - maxTailBytes);
        return Convert.ToHexString(payload[start..]);
    }

    private sealed class BrowserHttpLayerContentProvider(
        IReadOnlyDictionary<string, string> digestToBlobUrl,
        int timeoutMs) : ILayerContentProvider
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<BlobWindowKey, byte[]> _windows = new();
        private readonly LinkedList<BlobWindowKey> _lru = [];
        private readonly Dictionary<BlobWindowKey, LinkedListNode<BlobWindowKey>> _lruNodes = new();

        private const int MaxCachedWindows = 64;

        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            if (entry.Type != InodeType.File)
                return true;

            var hasBlobBacking = entry.DataOffset >= 0 && !string.IsNullOrWhiteSpace(entry.BlobDigest);
            if (!hasBlobBacking && entry.InlineData != null)
            {
                if (offset >= entry.InlineData.Length)
                    return true;

                var remaining = entry.InlineData.Length - (int)offset;
                var toCopy = Math.Min(buffer.Length, remaining);
                entry.InlineData.AsSpan((int)offset, toCopy).CopyTo(buffer);
                bytesRead = toCopy;
                return true;
            }

            if (entry.DataOffset < 0 || string.IsNullOrWhiteSpace(entry.BlobDigest))
                return false;
            if (!digestToBlobUrl.TryGetValue(entry.BlobDigest, out var blobUrl))
                return false;
            if (offset < 0)
                return false;
            if ((ulong)offset >= entry.Size)
                return true;

            var remainingInEntry = (long)entry.Size - offset;
            if (remainingInEntry <= 0)
                return true;

            var maxReadable = (int)Math.Min(buffer.Length, remainingInEntry);
            if (maxReadable <= 0)
                return true;

            var absoluteOffset = entry.DataOffset + offset;
            var windowStart = AlignDown(absoluteOffset, MinimumReadAheadBytes);
            var offsetInWindow = checked((int)(absoluteOffset - windowStart));
            var requiredBytes = offsetInWindow + maxReadable;
            var fetchLength = AlignUp(Math.Max(MinimumReadAheadBytes, requiredBytes), MinimumReadAheadBytes);
            var cacheKey = new BlobWindowKey(entry.BlobDigest, windowStart, fetchLength);

            if (!TryGetWindow(cacheKey, out var cachedWindow))
            {
                if (!TryFetchWindow(blobUrl, windowStart, fetchLength, out cachedWindow))
                    return false;
                StoreWindow(cacheKey, cachedWindow);
            }

            if (offsetInWindow >= cachedWindow.Length)
                return true;

            var available = Math.Min(maxReadable, cachedWindow.Length - offsetInWindow);
            if (available <= 0)
                return true;

            cachedWindow.AsSpan(offsetInWindow, available).CopyTo(buffer);
            bytesRead = available;
            return true;
        }

        private bool TryFetchWindow(string blobUrl, long windowStart, int fetchLength, out byte[] window)
        {
            window = [];
            var temp = new byte[fetchLength];
            if (!BrowserHttpRpc.TryReadRange(blobUrl, windowStart, temp, out var bytesRead, timeoutMs))
                return false;

            window = bytesRead == temp.Length ? temp : temp[..bytesRead];
            return true;
        }

        private bool TryGetWindow(BlobWindowKey key, out byte[] window)
        {
            lock (_sync)
            {
                if (!_windows.TryGetValue(key, out window!))
                    return false;
                TouchWindow(key);
                return true;
            }
        }

        private void StoreWindow(BlobWindowKey key, byte[] window)
        {
            lock (_sync)
            {
                _windows[key] = window;
                TouchWindow(key);
                while (_windows.Count > MaxCachedWindows)
                    EvictLeastRecentlyUsedWindow();
            }
        }

        private void TouchWindow(BlobWindowKey key)
        {
            if (_lruNodes.TryGetValue(key, out var existingNode))
            {
                _lru.Remove(existingNode);
            }
            else
            {
                existingNode = new LinkedListNode<BlobWindowKey>(key);
                _lruNodes[key] = existingNode;
            }

            _lru.AddFirst(existingNode);
        }

        private void EvictLeastRecentlyUsedWindow()
        {
            var node = _lru.Last;
            if (node == null)
                return;

            _lru.RemoveLast();
            _lruNodes.Remove(node.Value);
            _windows.Remove(node.Value);
        }

        private static long AlignDown(long value, int alignment)
        {
            return value - value % alignment;
        }

        private static int AlignUp(int value, int alignment)
        {
            if (value <= 0)
                return alignment;
            var remainder = value % alignment;
            return remainder == 0 ? value : value + (alignment - remainder);
        }

        private readonly record struct BlobWindowKey(string BlobDigest, long WindowStart, int WindowLength);
    }
}