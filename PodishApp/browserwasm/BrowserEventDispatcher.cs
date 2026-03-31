using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.Versioning;

namespace PodishApp.BrowserWasm;

[SupportedOSPlatform("browser")]
internal sealed class BrowserEventDispatcher
{
    private readonly ConcurrentDictionary<(BrowserSabQueueKind Queue, int EventType), BrowserEventHandler> _handlers = new();

    public void Register(BrowserSabQueueKind queue, int eventType, BrowserEventHandler handler)
    {
        _handlers[(queue, eventType)] = handler;
    }

    public bool TryRegister(BrowserSabQueueKind queue, int eventType, BrowserEventHandler handler)
    {
        return _handlers.TryAdd((queue, eventType), handler);
    }

    public bool Unregister(BrowserSabQueueKind queue, int eventType)
    {
        return _handlers.TryRemove((queue, eventType), out _);
    }

    public int DispatchQueue(BrowserSabQueueKind queue, int maxPackets = 64, int maxPacketBytes = 64 * 1024)
    {
        if (maxPackets <= 0)
            return 0;

        var packetBuffer = ArrayPool<byte>.Shared.Rent(maxPacketBytes);
        var dispatched = 0;
        try
        {
            for (; dispatched < maxPackets; dispatched++)
            {
                var packetLength = BrowserSabInterop.PollPacketInto(queue, packetBuffer.AsSpan(0, maxPacketBytes));
                if (packetLength <= 0)
                    break;

                if (!BrowserSabInterop.TryParsePacket(packetBuffer.AsSpan(0, packetLength), out var eventType, out var payload))
                    break;

                if (_handlers.TryGetValue((queue, eventType), out var handler))
                    handler(payload);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packetBuffer);
        }

        return dispatched;
    }

    public int Emit(BrowserSabQueueKind queue, int eventType, ReadOnlySpan<byte> payload)
    {
        return BrowserSabInterop.WritePacketFromMemory(queue, eventType, payload);
    }

    public static bool TryParseResize(ReadOnlySpan<byte> payload, out ushort rows, out ushort cols)
    {
        rows = 0;
        cols = 0;
        if (payload.Length < 4)
            return false;

        rows = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        cols = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        return true;
    }
}

internal delegate void BrowserEventHandler(ReadOnlySpan<byte> payload);

[SupportedOSPlatform("browser")]
internal enum BrowserSabQueueKind
{
    Input = 1,
    Output = 2,
    Log = 3
}
