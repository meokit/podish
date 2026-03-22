using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Silk.NET.SDL;

namespace Podish.Cli.Pulse;

internal sealed unsafe class Sdl3AudioSink : IDisposable
{
    private sealed record StreamRegistration(PlaybackStreamState State, Action ProgressCallback);

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Sdl _sdl;
    private readonly AudioCallback _audioCallback;
    private readonly Dictionary<uint, StreamRegistration> _streams = new();
    private GCHandle _selfHandle;
    private bool _initialized;
    private uint _deviceId;
    private float[] _mixScratch = Array.Empty<float>();

    public Sdl3AudioSink(ILogger logger)
    {
        _logger = logger;
        _sdl = Sdl.GetApi();
        _audioCallback = OnAudioCallback;
        DefaultSampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000);
    }

    public SampleSpec DefaultSampleSpec { get; }

    public void EnsureFormat(SampleSpec sampleSpec)
    {
        if (!PulseServerState.IsSupported(sampleSpec))
            throw new InvalidOperationException(
                $"Unsupported playback format for SDL sink: {sampleSpec.Format}/{sampleSpec.Channels}/{sampleSpec.SampleRate}");

        lock (_gate)
        {
            if (!_initialized)
            {
                if (_sdl.Init(Sdl.InitAudio) != 0)
                    throw new InvalidOperationException(
                        $"SDL audio init failed: {Marshal.PtrToStringUTF8((nint)_sdl.GetError())}");
                _initialized = true;
            }

            if (!_selfHandle.IsAllocated)
                _selfHandle = GCHandle.Alloc(this);

            if (_deviceId == 0)
            {
                AudioSpec desired = default;
                desired.Freq = (int)DefaultSampleSpec.SampleRate;
                desired.Format = Sdl.AudioS16Lsb;
                desired.Channels = DefaultSampleSpec.Channels;
                desired.Samples = 1024;
                desired.Callback = _audioCallback;
                desired.Userdata = (void*)GCHandle.ToIntPtr(_selfHandle);

                AudioSpec obtained = default;
                _deviceId = _sdl.OpenAudioDevice((byte*)0, 0, &desired, &obtained, 0);
                if (_deviceId == 0)
                    throw new InvalidOperationException(
                        $"SDL open audio device failed: {Marshal.PtrToStringUTF8((nint)_sdl.GetError())}");

                _logger.LogInformation(
                    "{Prefix} opened SDL audio device id={DeviceId} format={Format} channels={Channels} rate={Rate} samples={Samples}",
                    PulseServerLogging.Audio, _deviceId, DefaultSampleSpec.Format, DefaultSampleSpec.Channels,
                    DefaultSampleSpec.SampleRate, obtained.Samples);
            }

            UpdateDevicePausedLocked();
        }
    }

    public void AttachStream(PlaybackStreamState stream, Action playbackProgressCallback)
    {
        lock (_gate)
        {
            _streams[stream.ChannelIndex] = new StreamRegistration(stream, playbackProgressCallback);
            UpdateDevicePausedLocked();
        }
    }

    public void DetachStream(uint channelIndex)
    {
        lock (_gate)
        {
            _streams.Remove(channelIndex);
            UpdateDevicePausedLocked();
        }
    }

    public void NotifyStreamStateChanged()
    {
        lock (_gate)
            UpdateDevicePausedLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_deviceId != 0)
            {
                _sdl.CloseAudioDevice(_deviceId);
                _deviceId = 0;
            }

            if (_initialized)
            {
                _sdl.QuitSubSystem(Sdl.InitAudio);
                _initialized = false;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }
    }

    private void UpdateDevicePausedLocked()
    {
        if (_deviceId == 0)
            return;

        bool shouldPause = true;
        foreach (StreamRegistration registration in _streams.Values)
        {
            if (!registration.State.Corked && registration.State.HasPendingOutput)
            {
                shouldPause = false;
                break;
            }
        }

        _sdl.PauseAudioDevice(_deviceId, shouldPause ? 1 : 0);
    }

    private static void OnAudioCallback(void* userdata, byte* stream, int len)
    {
        if (userdata == null || stream == null || len <= 0)
            return;

        var handle = GCHandle.FromIntPtr((nint)userdata);
        if (handle.Target is not Sdl3AudioSink sink)
            return;

        sink.FillAudioBuffer(new Span<byte>(stream, len));
    }

    private void FillAudioBuffer(Span<byte> destination)
    {
        destination.Clear();

        lock (_gate)
        {
            if (_streams.Count == 0)
                return;

            int frames = destination.Length / (DefaultSampleSpec.Channels * sizeof(short));
            int sampleCount = frames * DefaultSampleSpec.Channels;
            if (_mixScratch.Length < sampleCount)
                _mixScratch = new float[sampleCount];

            Span<float> mix = _mixScratch.AsSpan(0, sampleCount);
            mix.Clear();

            bool anyMixed = false;
            foreach (StreamRegistration registration in _streams.Values)
            {
                int mixedFrames = registration.State.AudioStream.MixInto(mix, frames);
                if (mixedFrames > 0)
                    anyMixed = true;
            }

            if (anyMixed)
                PolyfillAudioMixer.WriteS16LeStereo(destination, mix, frames);

            foreach (StreamRegistration registration in _streams.Values)
            {
                if (anyMixed || registration.State.PendingDrainSequence != null || registration.State.ShouldRequestMore())
                    registration.ProgressCallback();
            }

            UpdateDevicePausedLocked();
        }
    }
}
