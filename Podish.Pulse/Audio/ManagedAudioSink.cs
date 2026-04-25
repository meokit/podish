namespace Podish.Pulse.Audio;

public abstract class ManagedAudioSink : IPulseAudioSink
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, StreamRegistration> _streams = new();
    private float _masterGain = 1.0f;
    private float[] _mixScratch = Array.Empty<float>();
    private bool _muted;

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

        lock (_gate)
        {
            EnsureBackendFormatCore(sampleSpec);
            UpdateBackendPausedLocked();
        }
    }

    public void AttachStream(PlaybackStreamState stream, Action playbackProgressCallback)
    {
        lock (_gate)
        {
            _streams[stream.ChannelIndex] = new StreamRegistration(stream, playbackProgressCallback);
            UpdateBackendPausedLocked();
        }
    }

    public void DetachStream(uint channelIndex)
    {
        lock (_gate)
        {
            _streams.Remove(channelIndex);
            UpdateBackendPausedLocked();
        }
    }

    public void NotifyStreamStateChanged()
    {
        lock (_gate)
        {
            UpdateBackendPausedLocked();
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
        lock (_gate)
        {
            DisposeCore();
        }
    }

    protected void RenderAudio(Span<byte> destination)
    {
        destination.Clear();

        lock (_gate)
        {
            if (_streams.Count == 0)
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
                    anyMixed = true;
            }

            if (anyMixed && (_muted || _masterGain != 1.0f))
                for (var i = 0; i < sampleCount; i++)
                    mix[i] *= _masterGain;

            if (anyMixed)
                AudioMixer.WriteS16LeStereo(destination, mix, frames);

            foreach (var registration in _streams.Values)
                if (anyMixed || registration.State.PendingDrainSequence != null ||
                    registration.State.ShouldRequestMore())
                    registration.ProgressCallback();

            UpdateBackendPausedLocked();
        }
    }

    protected abstract void EnsureBackendFormatCore(SampleSpec sampleSpec);
    protected abstract void SetBackendPausedCore(bool paused);
    protected abstract void DisposeCore();

    private void UpdateBackendPausedLocked()
    {
        var shouldPause = true;
        foreach (var registration in _streams.Values)
            if (!registration.State.Corked && registration.State.HasPendingOutput)
            {
                shouldPause = false;
                break;
            }

        SetBackendPausedCore(shouldPause);
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

    private sealed record StreamRegistration(PlaybackStreamState State, Action ProgressCallback);
}