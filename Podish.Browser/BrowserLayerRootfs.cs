using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Runtime.Versioning;
using Fiberish.Core;
using Fiberish.Native;
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
        int timeoutMs) : IAsyncLayerContentProvider
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<BlobWindowKey, byte[]> _windows = new();
        private readonly LinkedList<BlobWindowKey> _lru = [];
        private readonly Dictionary<BlobWindowKey, LinkedListNode<BlobWindowKey>> _lruNodes = new();

        private const int MaxCachedWindows = 64;

        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            var count = GetReadLength(entry, offset, buffer.Length);
            if (count <= 0) return count == 0;
            if (TryReadInline(entry, offset, buffer[..count], out bytesRead)) return true;
            if (!TryGetReadWindow(entry, offset, count, out var key, out var url, out var skip)) return false;

            if (!TryGetWindow(key, out var window))
            {
                if (!TryFetchWindow(url, key.WindowStart, key.WindowLength, out window)) return false;
                StoreWindow(key, window);
            }

            bytesRead = CopyWindow(window, skip, buffer[..count]);
            return true;
        }

        public async ValueTask<int> ReadAsync(LayerIndexEntry entry, long offset, Memory<byte> buffer, FiberTask task,
            bool noWait)
        {
            var count = GetReadLength(entry, offset, buffer.Length);
            if (count <= 0) return count;
            if (TryReadInline(entry, offset, buffer.Span[..count], out var bytesRead)) return bytesRead;
            if (!TryGetReadWindow(entry, offset, count, out var key, out var url, out var skip)) return -(int)Errno.EIO;

            if (!TryGetWindow(key, out var window))
            {
                if (noWait) return -(int)Errno.EAGAIN;
                var received = new byte[key.WindowLength];
                var result = await BrowserHttpRpc.ReadRangeAsync(url, key.WindowStart, received, task, timeoutMs);
                if (result == -(int)Errno.ERESTARTSYS) return result;
                if (result < 0) return -(int)Errno.EIO;
                window = result == received.Length ? received : received[..result];
                StoreWindow(key, window);
            }

            return CopyWindow(window, skip, buffer.Span[..count]);
        }

        private static int GetReadLength(LayerIndexEntry entry, long offset, int bufferLength)
        {
            if (offset < 0) return -(int)Errno.EINVAL;
            if (entry.Type != InodeType.File || (ulong)offset >= entry.Size) return 0;
            return (int)Math.Min((ulong)bufferLength, entry.Size - (ulong)offset);
        }

        private static bool TryReadInline(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            if (entry.DataOffset >= 0 && !string.IsNullOrWhiteSpace(entry.BlobDigest)) return false;
            if (entry.InlineData is not { } data) return false;
            if (offset < data.Length)
            {
                bytesRead = Math.Min(buffer.Length, data.Length - (int)offset);
                data.AsSpan((int)offset, bytesRead).CopyTo(buffer);
            }
            return true;
        }

        private bool TryGetReadWindow(LayerIndexEntry entry, long offset, int count,
            out BlobWindowKey key, out string url, out int skip)
        {
            key = default;
            url = string.Empty;
            skip = 0;
            if (entry.DataOffset < 0 || string.IsNullOrWhiteSpace(entry.BlobDigest) ||
                !digestToBlobUrl.TryGetValue(entry.BlobDigest, out var blobUrl))
                return false;

            url = blobUrl;
            var absoluteOffset = checked(entry.DataOffset + offset);
            var start = AlignDown(absoluteOffset, MinimumReadAheadBytes);
            skip = (int)(absoluteOffset - start);
            var length = AlignUp(checked(skip + count), MinimumReadAheadBytes);
            key = new BlobWindowKey(entry.BlobDigest, start, length);
            return true;
        }

        private static int CopyWindow(byte[] window, int skip, Span<byte> buffer)
        {
            var available = Math.Clamp(window.Length - skip, 0, buffer.Length);
            if (available > 0) window.AsSpan(skip, available).CopyTo(buffer);
            return available;
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
