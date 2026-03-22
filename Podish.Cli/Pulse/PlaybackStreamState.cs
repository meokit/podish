using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PlaybackStreamState
{
    private readonly object _gate = new();
    private readonly byte[] _ringBuffer;
    private int _readOffset;
    private int _writeOffset;
    private int _bufferedBytes;
    private int _pendingRequestedBytes;
    private ulong _receivedBytesTotal;

    public PlaybackStreamState(uint channelIndex, CreatePlaybackStreamParams parameters, string? clientName)
    {
        PlaybackBufferAttr normalizedBufferAttr = NormalizeBufferAttr(parameters.BufferAttr, parameters.SampleSpec);

        ChannelIndex = channelIndex;
        StreamIndex = channelIndex;
        DeviceIndex = parameters.DeviceIndex ?? 0;
        SampleSpec = parameters.SampleSpec;
        ChannelMap = parameters.ChannelMap ?? (parameters.SampleSpec.Channels == 1 ? ChannelMap.Mono() : ChannelMap.Stereo());
        BufferAttr = normalizedBufferAttr;
        Props = parameters.Props ?? new Props();
        Volume = parameters.Volume ?? ChannelVolume.Norm(parameters.SampleSpec.Channels);
        ClientName = clientName ?? "unknown";
        _ringBuffer = new byte[ComputeRingCapacity(normalizedBufferAttr, parameters.SampleSpec)];
        Corked = parameters.Flags.HasFlag(PlaybackStreamFlags.StartCorked) ||
                 parameters.Flags.HasFlag(PlaybackStreamFlags.StartPaused);
    }

    public uint ChannelIndex { get; }
    public uint StreamIndex { get; }
    public uint DeviceIndex { get; }
    public SampleSpec SampleSpec { get; }
    public ChannelMap ChannelMap { get; }
    public PlaybackBufferAttr BufferAttr { get; }
    public Props Props { get; }
    public ChannelVolume Volume { get; }
    public string ClientName { get; }
    public string? StreamName { get; private set; }
    public bool Corked { get; private set; }
    public bool Triggered { get; private set; }
    public uint? PendingDrainSequence { get; private set; }
    public bool StartedNotified { get; private set; }
    public int Capacity => _ringBuffer.Length;
    public int BufferedBytes
    {
        get
        {
            lock (_gate)
                return _bufferedBytes;
        }
    }

    public int PendingRequestedBytes
    {
        get
        {
            lock (_gate)
                return _pendingRequestedBytes;
        }
    }

    public int RequestBytesHint
    {
        get
        {
            if (BufferAttr.MinRequest > 0)
                return (int)BufferAttr.MinRequest;

            int frameSize = Math.Max(1, SampleSpec.BytesPerSample * SampleSpec.Channels);
            return Math.Max(frameSize * 256, 4096);
        }
    }

    public uint InitialRequestedBytes => BufferAttr.Prebuffer > 0 ? BufferAttr.Prebuffer : (uint)RequestBytesHint;

    public int TargetBytesHint
    {
        get
        {
            if (BufferAttr.TargetLength > 0)
                return (int)BufferAttr.TargetLength;

            int request = RequestBytesHint;
            return Math.Max(request * 2, 8192);
        }
    }

    public void SetStreamName(string? streamName)
    {
        StreamName = streamName;
    }

    public void SetCorked(bool corked)
    {
        Corked = corked;
    }

    public void Trigger()
    {
        Triggered = true;
        Corked = false;
    }

    public void QueueDrain(uint sequence)
    {
        PendingDrainSequence = sequence;
    }

    public void ClearPendingDrain()
    {
        PendingDrainSequence = null;
    }

    public int Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return BufferedBytes;

        lock (_gate)
        {
            int writable = Math.Min(data.Length, _ringBuffer.Length - _bufferedBytes);
            if (writable > 0)
            {
                int first = Math.Min(writable, _ringBuffer.Length - _writeOffset);
                data[..first].CopyTo(_ringBuffer.AsSpan(_writeOffset, first));
                int remaining = writable - first;
                if (remaining > 0)
                    data.Slice(first, remaining).CopyTo(_ringBuffer.AsSpan(0, remaining));

                _writeOffset = (_writeOffset + writable) % _ringBuffer.Length;
                _bufferedBytes += writable;
            }

            _pendingRequestedBytes = Math.Max(0, _pendingRequestedBytes - data.Length);
            _receivedBytesTotal += (ulong)data.Length;
            return _bufferedBytes;
        }
    }

    public bool TryMarkStarted()
    {
        lock (_gate)
        {
            if (StartedNotified)
                return false;

            ulong threshold = BufferAttr.Prebuffer > 0 ? BufferAttr.Prebuffer : (uint)RequestBytesHint;
            if (_receivedBytesTotal < threshold)
                return false;

            StartedNotified = true;
            return true;
        }
    }

    public void RecordRequest(int bytes)
    {
        if (bytes <= 0)
            return;

        lock (_gate)
        {
            _pendingRequestedBytes += bytes;
        }
    }

    public int CopyInto(Span<byte> destination)
    {
        lock (_gate)
        {
            int written = Math.Min(destination.Length, _bufferedBytes);
            if (written <= 0)
                return 0;

            int first = Math.Min(written, _ringBuffer.Length - _readOffset);
            _ringBuffer.AsSpan(_readOffset, first).CopyTo(destination[..first]);
            int remaining = written - first;
            if (remaining > 0)
                _ringBuffer.AsSpan(0, remaining).CopyTo(destination.Slice(first, remaining));

            _readOffset = (_readOffset + written) % _ringBuffer.Length;
            _bufferedBytes -= written;
            return written;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readOffset = 0;
            _writeOffset = 0;
            _bufferedBytes = 0;
            _pendingRequestedBytes = 0;
        }

        PendingDrainSequence = null;
    }

    public bool ShouldRequestMore(int backendQueuedBytes)
    {
        if (Corked)
            return false;

        return BufferedBytes + backendQueuedBytes + PendingRequestedBytes < TargetBytesHint;
    }

    private static PlaybackBufferAttr CreateDefaultBufferAttr(SampleSpec sampleSpec)
    {
        _ = sampleSpec;
        const uint target = 128u * 1024u;
        const uint minRequest = target / 8u;
        return new PlaybackBufferAttr
        {
            MaxLength = 4096u * 1024u,
            TargetLength = target,
            MinRequest = minRequest,
            Prebuffer = target,
            MinIncrement = minRequest,
        };
    }

    private static PlaybackBufferAttr NormalizeBufferAttr(PlaybackBufferAttr? bufferAttr, SampleSpec sampleSpec)
    {
        PlaybackBufferAttr attr = bufferAttr is null
            ? CreateDefaultBufferAttr(sampleSpec)
            : new PlaybackBufferAttr
            {
                MaxLength = bufferAttr.MaxLength,
                TargetLength = bufferAttr.TargetLength,
                MinRequest = bufferAttr.MinRequest,
                Prebuffer = bufferAttr.Prebuffer,
                MinIncrement = bufferAttr.MinIncrement,
            };

        PlaybackBufferAttr defaults = CreateDefaultBufferAttr(sampleSpec);

        if (attr.MaxLength == 0 || attr.MaxLength == uint.MaxValue)
            attr.MaxLength = defaults.MaxLength;
        if (attr.TargetLength == 0 || attr.TargetLength == uint.MaxValue)
            attr.TargetLength = defaults.TargetLength;
        if (attr.Prebuffer == uint.MaxValue)
            attr.Prebuffer = attr.TargetLength;
        if (attr.MinRequest == 0 || attr.MinRequest == uint.MaxValue)
            attr.MinRequest = defaults.MinRequest;
        if (attr.MinIncrement == 0 || attr.MinIncrement == uint.MaxValue)
            attr.MinIncrement = attr.MinRequest;

        return attr;
    }

    private static int ComputeRingCapacity(PlaybackBufferAttr bufferAttr, SampleSpec sampleSpec)
    {
        int target = bufferAttr.TargetLength > 0 ? (int)bufferAttr.TargetLength : 128 * 1024;
        int frameSize = Math.Max(1, sampleSpec.BytesPerSample * sampleSpec.Channels);
        int minRequest = bufferAttr.MinRequest > 0 ? (int)bufferAttr.MinRequest : Math.Max(frameSize * 256, 4096);
        int capacity = Math.Max(256 * 1024, target + (2 * minRequest));
        return AlignToFrame(capacity, frameSize);
    }

    private static int AlignToFrame(int bytes, int frameSize)
    {
        int size = Math.Max(1, frameSize);
        int remainder = bytes % size;
        return remainder == 0 ? bytes : bytes + (size - remainder);
    }
}
