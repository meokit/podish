namespace Podish.Pulse.Audio;

public sealed class PlaybackStreamState
{
    private readonly Lock _gate = new();
    private int _pendingRequestedBytes;
    private ulong _receivedBytesTotal;
    private int _requestInFlight;

    public PlaybackStreamState(uint channelIndex, uint streamIndex, CreatePlaybackStreamParams parameters,
        string? clientName)
    {
        var normalizedBufferAttr = NormalizeBufferAttr(parameters.BufferAttr, parameters.SampleSpec);

        ChannelIndex = channelIndex;
        StreamIndex = streamIndex;
        DeviceIndex = parameters.DeviceIndex ?? 0;
        SampleSpec = parameters.SampleSpec;
        ChannelMap = parameters.ChannelMap ??
                     (parameters.SampleSpec.Channels == 1 ? ChannelMap.Mono() : ChannelMap.Stereo());
        BufferAttr = normalizedBufferAttr;
        Props = parameters.Props ?? new Props();
        Volume = parameters.Volume ?? ChannelVolume.Norm(parameters.SampleSpec.Channels);
        ClientName = clientName ?? "unknown";
        Mute = parameters.Flags.HasFlag(PlaybackStreamFlags.StartMuted);
        Corked = parameters.Flags.HasFlag(PlaybackStreamFlags.StartCorked) ||
                 parameters.Flags.HasFlag(PlaybackStreamFlags.StartPaused);

        AudioStream = new AudioStream(
            channelIndex,
            parameters.SampleSpec,
            ChannelMap,
            ComputeRingCapacity(normalizedBufferAttr, parameters.SampleSpec),
            ComputeGain(Volume, Mute),
            Corked);
    }

    public uint ChannelIndex { get; }
    public uint StreamIndex { get; }
    public uint DeviceIndex { get; }
    public SampleSpec SampleSpec { get; }
    public ChannelMap ChannelMap { get; }
    public PlaybackBufferAttr BufferAttr { get; }
    public Props Props { get; }
    public ChannelVolume Volume { get; }
    public bool Mute { get; private set; }
    public string ClientName { get; }
    public AudioStream AudioStream { get; }
    public string? StreamName { get; private set; }
    public bool Corked { get; private set; }
    public bool Triggered { get; private set; }
    public uint? PendingDrainSequence { get; private set; }
    public bool StartedNotified { get; private set; }
    public int Capacity => AudioStream.Capacity;
    public int BufferedBytes => AudioStream.QueuedInputBytes;
    public int QueuedOutputEstimateBytes => AudioStream.QueuedOutputEstimateBytes;
    public bool HasPendingOutput => AudioStream.HasPendingOutput;

    public int PendingRequestedBytes
    {
        get
        {
            lock (_gate)
            {
                return _pendingRequestedBytes;
            }
        }
    }

    public int RequestBytesHint
    {
        get
        {
            if (BufferAttr.MinRequest > 0)
                return (int)BufferAttr.MinRequest;

            var frameSize = Math.Max(1, SampleSpec.BytesPerSample * SampleSpec.Channels);
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

            var request = RequestBytesHint;
            return Math.Max(request * 2, 8192);
        }
    }

    public void SetStreamName(string? streamName)
    {
        StreamName = streamName;
    }

    public void SetVolume(ChannelVolume volume)
    {
        lock (_gate)
        {
            var normalized = NormalizeChannelVolume(volume, SampleSpec.Channels);
            for (var i = 0; i < normalized.Channels; i++)
                Volume[i] = normalized[i];

            AudioStream.SetGain(ComputeGain(Volume, Mute));
        }
    }

    public void SetMute(bool mute)
    {
        lock (_gate)
        {
            Mute = mute;
            AudioStream.SetGain(ComputeGain(Volume, Mute));
        }
    }

    public void UpdateProps(PropsUpdateMode mode, Props props)
    {
        lock (_gate)
        {
            Props.Update(mode, props);
        }
    }

    public int RemoveProps(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            return Props.RemoveKeys(keys);
        }
    }

    public void SetCorked(bool corked)
    {
        Corked = corked;
        AudioStream.SetPaused(corked);
    }

    public void Trigger()
    {
        Triggered = true;
        Corked = false;
        AudioStream.SetPaused(false);
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

        var written = AudioStream.PutData(data);
        lock (_gate)
        {
            _pendingRequestedBytes = Math.Max(0, _pendingRequestedBytes - written);
            _receivedBytesTotal += (ulong)written;
        }

        return AudioStream.QueuedInputBytes;
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

    public bool TryBeginRequest()
    {
        return Interlocked.CompareExchange(ref _requestInFlight, 1, 0) == 0;
    }

    public void EndRequest()
    {
        Interlocked.Exchange(ref _requestInFlight, 0);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pendingRequestedBytes = 0;
        }

        PendingDrainSequence = null;
        AudioStream.Clear();
    }

    public bool ShouldRequestMore()
    {
        if (Corked)
            return false;

        if (!StartedNotified && BufferedBytes == 0)
            return true;

        return QueuedOutputEstimateBytes + PendingRequestedBytes < TargetBytesHint;
    }

    private static float ComputeGain(ChannelVolume volume, bool mute)
    {
        if (mute)
            return 0.0f;

        return ComputeAverageGain(volume);
    }

    private static float ComputeAverageGain(ChannelVolume volume)
    {
        if (volume.Channels == 0)
            return 1.0f;

        float total = 0;
        for (var i = 0; i < volume.Channels; i++)
            total += volume[i].ToLinear();
        return total / volume.Channels;
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
            MinIncrement = minRequest
        };
    }

    private static PlaybackBufferAttr NormalizeBufferAttr(PlaybackBufferAttr? bufferAttr, SampleSpec sampleSpec)
    {
        var attr = bufferAttr is null
            ? CreateDefaultBufferAttr(sampleSpec)
            : new PlaybackBufferAttr
            {
                MaxLength = bufferAttr.MaxLength,
                TargetLength = bufferAttr.TargetLength,
                MinRequest = bufferAttr.MinRequest,
                Prebuffer = bufferAttr.Prebuffer,
                MinIncrement = bufferAttr.MinIncrement
            };

        var defaults = CreateDefaultBufferAttr(sampleSpec);

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
        var target = bufferAttr.TargetLength > 0 ? (int)bufferAttr.TargetLength : 128 * 1024;
        var frameSize = Math.Max(1, sampleSpec.BytesPerSample * sampleSpec.Channels);
        var minRequest = bufferAttr.MinRequest > 0 ? (int)bufferAttr.MinRequest : Math.Max(frameSize * 256, 4096);
        var capacity = Math.Max(256 * 1024, target + 2 * minRequest);
        return AlignToFrame(capacity, frameSize);
    }

    private static int AlignToFrame(int bytes, int frameSize)
    {
        var size = Math.Max(1, frameSize);
        var remainder = bytes % size;
        return remainder == 0 ? bytes : bytes + (size - remainder);
    }

    private static ChannelVolume NormalizeChannelVolume(ChannelVolume requested, byte expectedChannels)
    {
        if (requested.Channels == expectedChannels)
            return CloneChannelVolume(requested);

        if (requested.Channels == 1)
        {
            ChannelVolume expanded = new();
            var volume = requested[0];
            for (var i = 0; i < expectedChannels; i++)
                expanded.Push(volume);
            return expanded;
        }

        throw new ArgumentOutOfRangeException(nameof(requested),
            $"Volume channel count {requested.Channels} is incompatible with target channel count {expectedChannels}");
    }

    private static ChannelVolume CloneChannelVolume(ChannelVolume requested)
    {
        ChannelVolume clone = new();
        for (var i = 0; i < requested.Channels; i++)
            clone.Push(requested[i]);
        return clone;
    }
}