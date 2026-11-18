namespace Podish.Pulse.Audio;

public abstract class ManagedAudioSink : IPulseAudioSink
{
    private readonly Lock _backendGate = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, StreamRegistration> _streams = new();
    private float _masterGain = 1.0f;
    private float[] _mixScratch = Array.Empty<float>();
    private bool _muted;
    private volatile bool _disposed;

    protected ManagedAudioSink(SampleSpec defaultSampleSpec)
    {
        DefaultSampleSpec = defaultSampleSpec;
    }

    public SampleSpec DefaultSampleSpec { get; }

    public void EnsureFormat(SampleSpec sampleSpec)
    {
        if (!PulseAudioFormats.IsSupported(sampleSpec))
            throw new InvalidOperationException(
                $"Unsupported playback format: {sampleSpec.Format}/{sampleSpec.Channels}/{sampleSpec.SampleRate}");

        lock (_backendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureBackendFormatCore(sampleSpec);
        }
    }

    public void AttachStream(PlaybackStreamState stream, Action playbackProgressCallback)
    {
        lock (_gate)
        {
            if (!_disposed)
                _streams[stream.StreamIndex] = new StreamRegistration(stream, playbackProgressCallback);
        }
    }

    public void DetachStream(uint streamIndex)
    {
        lock (_gate)
        {
            _streams.Remove(streamIndex);
        }
    }

    public void SetMasterVolume(ChannelVolume volume, bool muted)
    {
        lock (_gate)
        {
            _masterGain = muted ? 0.0f : ComputeAverageGain(volume);
            _muted = muted;
        }
    }

    public void Dispose()
    {
        lock (_backendGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            // The backend can wait for a callback to finish here.
            DisposeCore();
        }

        lock (_gate)
        {
            _streams.Clear();
        }
    }

    protected void RenderAudio(Span<byte> destination)
    {
        destination.Clear();

        lock (_gate)
        {
            if (_disposed || _streams.Count == 0)
                return;

            var frames = destination.Length / (DefaultSampleSpec.Channels * sizeof(short));
            var sampleCount = frames * DefaultSampleSpec.Channels;
            if (_mixScratch.Length < sampleCount)
                _mixScratch = new float[sampleCount];

            var mix = _mixScratch.AsSpan(0, sampleCount);
            mix.Clear();

            var anyMixed = false;
            foreach (var registration in _streams.Values)
            {
                var mixedFrames = registration.State.AudioStream.MixInto(mix, frames);
                if (mixedFrames > 0)
                {
                    anyMixed = true;
                    registration.ProgressCallback();
                }
            }

            if (anyMixed && (_muted || _masterGain != 1.0f))
                for (var i = 0; i < sampleCount; i++)
                    mix[i] *= _masterGain;

            if (anyMixed)
                AudioMixer.WriteS16LeStereo(destination, mix, frames);
        }
    }

    protected abstract void EnsureBackendFormatCore(SampleSpec sampleSpec);
    protected abstract void DisposeCore();

    private static float ComputeAverageGain(ChannelVolume volume)
    {
        if (volume.Channels == 0)
            return 1.0f;

        float total = 0;
        for (var i = 0; i < volume.Channels; i++)
            total += volume[i].ToLinear();
        return total / volume.Channels;
    }

    private sealed record StreamRegistration(PlaybackStreamState State, Action ProgressCallback);
}