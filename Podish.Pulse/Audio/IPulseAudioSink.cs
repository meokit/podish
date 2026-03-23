using Podish.Pulse.Protocol;

namespace Podish.Pulse.Audio;

public interface IPulseAudioSink : IDisposable
{
    SampleSpec DefaultSampleSpec { get; }
    void EnsureFormat(SampleSpec sampleSpec);
    void AttachStream(PlaybackStreamState stream, Action playbackProgressCallback);
    void DetachStream(uint channelIndex);
    void NotifyStreamStateChanged();
    void SetMasterVolume(ChannelVolume volume, bool muted);
}
