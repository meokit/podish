using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using Fiberish.Core;
using Microsoft.Extensions.Logging;

namespace Podish.Browser;

[SupportedOSPlatform("browser")]
internal static class BrowserHttpRpc
{
    private const int StackUrlUtf8Threshold = 1024;
    private const int StreamChunkSize = BrowserSabInterop.HttpRpcResponseCapacity;

    public static unsafe int BeginStreamGet(string url, long rangeStart = -1, int rangeLength = -1, int timeoutMs = -1)
    {
        ArgumentNullException.ThrowIfNull(url);

        var rangeMode = BrowserSabInterop.HttpRpcRangeModeNone;
        var rangeStartLow = 0;
        var rangeStartHigh = 0;

        if (rangeStart >= 0)
        {
            rangeMode = rangeLength >= 0
                ? BrowserSabInterop.HttpRpcRangeModeBounded
                : BrowserSabInterop.HttpRpcRangeModeOpenEnded;
            SplitInt64(rangeStart, out rangeStartLow, out rangeStartHigh);
        }
        else if (rangeLength >= 0)
        {
            return BrowserSabInterop.HttpRpcResultInvalidRequest;
        }

        BrowserExports.EmitDiagnosticLog(LogLevel.Information,
            $"[http-rpc/cs] begin url={url} rangeStart={rangeStart} rangeLength={rangeLength} timeoutMs={timeoutMs}");

        var urlByteCount = Encoding.UTF8.GetByteCount(url);
        byte[]? rented = null;

        try
        {
            var urlUtf8 = urlByteCount <= StackUrlUtf8Threshold
                ? stackalloc byte[StackUrlUtf8Threshold]
                : (rented = ArrayPool<byte>.Shared.Rent(urlByteCount));
            var written = Encoding.UTF8.GetBytes(url, urlUtf8);
            fixed (byte* urlPtr = urlUtf8)
            {
                return BrowserSabInterop.HttpRpcBeginStreamGet(
                    (nint)urlPtr,
                    written,
                    rangeMode,
                    rangeStartLow,
                    rangeStartHigh,
                    rangeLength,
                    timeoutMs);
            }
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static byte[] ReadAllBytes(string url, int timeoutMs = -1)
    {
        var requestId = BeginStreamGet(url, timeoutMs: timeoutMs);
        if (requestId <= 0)
            throw new InvalidOperationException($"Failed to start HTTP request for '{url}': {requestId}");

        BrowserExports.EmitDiagnosticLog(LogLevel.Information,
            $"[http-rpc/cs] read-all requestId={requestId} url={url} chunkCapacity={StreamChunkSize}");

        var writer = new ArrayBufferWriter<byte>();
        var scratch = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        try
        {
            while (true)
            {
                var bytesRead = WaitAndReadStreamChunk(requestId, scratch.AsSpan(0, StreamChunkSize), timeoutMs);
                BrowserExports.EmitDiagnosticLog(LogLevel.Information,
                    $"[http-rpc/cs] read-all-chunk requestId={requestId} bytesRead={bytesRead}");
                if (bytesRead == 0)
                    return writer.WrittenSpan.ToArray();
                if (bytesRead < 0)
                    throw new InvalidOperationException($"HTTP read failed for '{url}': {bytesRead}");

                writer.Write(scratch.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            CloseRequest(requestId);
        }
    }

    public static bool TryReadRange(string url, long rangeStart, Span<byte> destination, out int bytesRead,
        int timeoutMs = -1)
    {
        bytesRead = 0;
        if (rangeStart < 0)
            return false;
        if (destination.IsEmpty)
            return true;

        var requestId = BeginStreamGet(url, rangeStart, destination.Length, timeoutMs);
        if (requestId <= 0)
            return false;

        BrowserExports.EmitDiagnosticLog(LogLevel.Information,
            $"[http-rpc/cs] read-range requestId={requestId} url={url} rangeStart={rangeStart} destinationLength={destination.Length}");

        try
        {
            while (bytesRead < destination.Length)
            {
                var chunkRead = WaitAndReadStreamChunk(requestId, destination[bytesRead..], timeoutMs);
                BrowserExports.EmitDiagnosticLog(LogLevel.Information,
                    $"[http-rpc/cs] read-range-chunk requestId={requestId} chunkRead={chunkRead} accumulated={bytesRead}");
                if (chunkRead == 0)
                    return true;
                if (chunkRead < 0)
                    return false;

                bytesRead += chunkRead;
            }

            return true;
        }
        finally
        {
            CloseRequest(requestId);
        }
    }

    public static int GetRequestFlags(int requestId)
    {
        if (requestId <= 0)
            return BrowserSabInterop.HttpRpcResultInvalidRequest;

        return BrowserSabInterop.HttpRpcGetRequestFlags(requestId);
    }

    public static unsafe int TryReadStreamChunk(int requestId, Span<byte> destination)
    {
        if (requestId <= 0)
            return BrowserSabInterop.HttpRpcResultInvalidRequest;

        if (destination.IsEmpty)
            return BrowserSabInterop.HttpRpcTryReadStreamChunkIntoMemory(requestId, 0, 0);

        fixed (byte* destinationPtr = destination)
        {
            return BrowserSabInterop.HttpRpcTryReadStreamChunkIntoMemory(
                requestId,
                (nint)destinationPtr,
                destination.Length);
        }
    }

    public static int CloseRequest(int requestId)
    {
        if (requestId <= 0)
            return BrowserSabInterop.HttpRpcResultInvalidRequest;

        return BrowserSabInterop.HttpRpcCloseRequest(requestId);
    }

    public static int WaitAndReadStreamChunk(int requestId, Span<byte> destination, int timeoutMs = -1)
    {
        var startedAt = Environment.TickCount64;
        while (true)
        {
            var result = TryReadStreamChunk(requestId, destination);
            if (result != BrowserSabInterop.HttpRpcResultPending)
                return result;

            var remainingTimeout = ComputeRemainingTimeout(startedAt, timeoutMs);
            if (timeoutMs >= 0 && remainingTimeout == 0)
                return BrowserSabInterop.HttpRpcResultTimeout;

            BrowserSchedulerHostBridge.WaitForEvent(remainingTimeout);
        }
    }

    private static int ComputeRemainingTimeout(long startedAt, int timeoutMs)
    {
        if (timeoutMs < 0)
            return -1;

        var elapsed = Environment.TickCount64 - startedAt;
        if (elapsed >= timeoutMs)
            return 0;

        return timeoutMs - (int)elapsed;
    }

    private static void SplitInt64(long value, out int low, out int high)
    {
        var bits = unchecked((ulong)value);
        low = unchecked((int)(bits & 0xFFFF_FFFF));
        high = unchecked((int)(bits >> 32));
    }
}