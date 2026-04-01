using System.Buffers.Binary;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Podish.Browser;

[SupportedOSPlatform("browser")]
internal static partial class BrowserSabInterop
{
    internal const int LogChunkSize = 4096;
    internal const int QueuePacketBufferSize = 64 * 1024;
    internal const int HttpRpcResponseCapacity = 128 * 1024;
    internal const int LogFlagBegin = 1 << 0;
    internal const int LogFlagEnd = 1 << 1;
    internal const int IrqInputReady = 1 << 0;
    internal const int IrqOutputReady = 1 << 1;
    internal const int IrqOutputDrained = 1 << 2;
    internal const int IrqTimer = 1 << 3;
    internal const int IrqSchedulerWake = 1 << 4;
    internal const int IrqHttpRpc = 1 << 6;

    internal const int EventInputBytes = 1;
    internal const int EventOutputBytes = 2;
    internal const int EventResize = 3;
    internal const int EventControl = 4;
    internal const int EventLogMessage = 5;
    internal const int EventHttpRequest = 6;

    internal const int HttpRpcResultOk = 0;
    internal const int HttpRpcResultTimeout = -1;
    internal const int HttpRpcResultNetworkError = -2;
    internal const int HttpRpcResultTooLarge = -3;
    internal const int HttpRpcResultPending = -4;
    internal const int HttpRpcResultNoFreeSlot = -5;
    internal const int HttpRpcResultInvalidRequest = -6;
    internal const int HttpRpcResultCancelled = -7;
    internal const int HttpRpcResultUrlTooLong = -8;

    internal const int HttpRpcFlagStarted = 1 << 0;
    internal const int HttpRpcFlagChunkReady = 1 << 1;
    internal const int HttpRpcFlagEof = 1 << 2;
    internal const int HttpRpcFlagError = 1 << 3;
    internal const int HttpRpcFlagCancelled = 1 << 4;

    internal const int HttpRpcRangeModeNone = 0;
    internal const int HttpRpcRangeModeOpenEnded = 1;
    internal const int HttpRpcRangeModeBounded = 2;

    [JSImport("pollInterrupt", "podish-worker.mjs")]
    internal static partial int PollInterrupt(int mask = -1);

    [JSImport("waitForInterruptSync", "podish-worker.mjs")]
    internal static partial int WaitForInterrupt(int mask = -1, int timeoutMs = -1);

    [JSImport("pollInputPacketInto", "podish-worker.mjs")]
    internal static unsafe partial int PollInputPacketInto(nint buffer, int maxBytes = QueuePacketBufferSize);

    [JSImport("pollOutputPacketInto", "podish-worker.mjs")]
    internal static unsafe partial int PollOutputPacketInto(nint buffer, int maxBytes = QueuePacketBufferSize);

    [JSImport("pollLogPacketInto", "podish-worker.mjs")]
    internal static unsafe partial int PollLogPacketInto(nint buffer, int maxBytes = QueuePacketBufferSize);

    [JSImport("writeInputPacketFromMemory", "podish-worker.mjs")]
    internal static unsafe partial int WriteInputPacketFromMemory(int eventType, nint buffer, int length);

    [JSImport("writeOutputPacketFromMemory", "podish-worker.mjs")]
    internal static unsafe partial int WriteOutputPacketFromMemory(int eventType, nint buffer, int length);

    [JSImport("writeLogPacketFromMemory", "podish-worker.mjs")]
    internal static unsafe partial int WriteLogPacketFromMemory(int eventType, nint buffer, int length, int flags);

    [JSImport("httpRpcBeginStreamGet", "podish-worker.mjs")]
    internal static unsafe partial int HttpRpcBeginStreamGet(
        nint urlUtf8,
        int urlUtf8Length,
        int rangeMode,
        int rangeStartLow,
        int rangeStartHigh,
        int rangeLength,
        int timeoutMs = -1);

    [JSImport("httpRpcGetRequestFlags", "podish-worker.mjs")]
    internal static partial int HttpRpcGetRequestFlags(int requestId);

    [JSImport("httpRpcTryReadStreamChunkIntoMemory", "podish-worker.mjs")]
    internal static unsafe partial int HttpRpcTryReadStreamChunkIntoMemory(
        int requestId,
        nint destination,
        int destinationLength);

    [JSImport("httpRpcCloseRequest", "podish-worker.mjs")]
    internal static partial int HttpRpcCloseRequest(int requestId);

    internal static bool TryParsePacket(ReadOnlySpan<byte> rawPacket, out int eventType, out ReadOnlySpan<byte> payload)
    {
        eventType = 0;
        payload = default;

        if (rawPacket.Length < 8)
            return false;

        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(rawPacket[..4]);
        if (totalLength < 8 || totalLength > rawPacket.Length)
            return false;

        eventType = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawPacket.Slice(4, 4));
        payload = rawPacket.Slice(8, (int)totalLength - 8);
        return true;
    }

    internal static unsafe int PollPacketInto(BrowserSabQueueKind queue, Span<byte> destination)
    {
        if (destination.IsEmpty)
            return 0;

        fixed (byte* ptr = destination)
        {
            return queue switch
            {
                BrowserSabQueueKind.Input => PollInputPacketInto((nint)ptr, destination.Length),
                BrowserSabQueueKind.Output => PollOutputPacketInto((nint)ptr, destination.Length),
                BrowserSabQueueKind.Log => PollLogPacketInto((nint)ptr, destination.Length),
                _ => 0
            };
        }
    }

    internal static unsafe int WritePacketFromMemory(BrowserSabQueueKind queue, int eventType,
        ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return queue switch
            {
                BrowserSabQueueKind.Input => WriteInputPacketFromMemory(eventType, 0, 0),
                BrowserSabQueueKind.Output => WriteOutputPacketFromMemory(eventType, 0, 0),
                BrowserSabQueueKind.Log => WriteLogPacketFromMemory(eventType, 0, 0, 0),
                _ => 0
            };

        fixed (byte* ptr = payload)
        {
            return queue switch
            {
                BrowserSabQueueKind.Input => WriteInputPacketFromMemory(eventType, (nint)ptr, payload.Length),
                BrowserSabQueueKind.Output => WriteOutputPacketFromMemory(eventType, (nint)ptr, payload.Length),
                BrowserSabQueueKind.Log => WriteLogPacketFromMemory(eventType, (nint)ptr, payload.Length,
                    LogFlagBegin | LogFlagEnd),
                _ => 0
            };
        }
    }
}
