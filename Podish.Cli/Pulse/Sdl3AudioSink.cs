using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Silk.NET.SDL;

namespace Podish.Cli.Pulse;

internal sealed unsafe class Sdl3AudioSink : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Sdl _sdl;
    private readonly AudioCallback _audioCallback;
    private GCHandle _selfHandle;
    private bool _initialized;
    private uint _deviceId;
    private SampleSpec _openedSpec;
    private PlaybackStreamState? _activeStream;
    private Action? _playbackProgressCallback;

    public Sdl3AudioSink(ILogger logger)
    {
        _logger = logger;
        _sdl = Sdl.GetApi();
        _audioCallback = OnAudioCallback;
        DefaultSampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000);
        _openedSpec = DefaultSampleSpec;
    }

    public SampleSpec DefaultSampleSpec { get; }

    public int QueuedBytes
    {
        get { return 0; }
    }

    public void EnsureFormat(SampleSpec sampleSpec)
    {
        lock (_gate)
        {
            if (_deviceId != 0 &&
                _openedSpec.Format == sampleSpec.Format &&
                _openedSpec.Channels == sampleSpec.Channels &&
                _openedSpec.SampleRate == sampleSpec.SampleRate)
            {
                UpdateDevicePausedLocked();
                return;
            }

            if (!_initialized)
            {
                if (_sdl.Init(Sdl.InitAudio) != 0)
                    throw new InvalidOperationException(
                        $"SDL audio init failed: {Marshal.PtrToStringUTF8((nint)_sdl.GetError())}");
                _initialized = true;
            }

            if (!_selfHandle.IsAllocated)
                _selfHandle = GCHandle.Alloc(this);

            if (_deviceId != 0)
            {
                _logger.LogDebug("{Prefix} reopening audio device for format={Format} channels={Channels} rate={Rate}",
                    PulseServerLogging.Audio, sampleSpec.Format, sampleSpec.Channels, sampleSpec.SampleRate);
                _sdl.CloseAudioDevice(_deviceId);
                _deviceId = 0;
            }

            AudioSpec desired = default;
            desired.Freq = (int)sampleSpec.SampleRate;
            desired.Format = Sdl.AudioS16Lsb;
            desired.Channels = sampleSpec.Channels;
            desired.Samples = 1024;
            desired.Callback = _audioCallback;
            desired.Userdata = (void*)GCHandle.ToIntPtr(_selfHandle);

            AudioSpec obtained = default;
            _deviceId = _sdl.OpenAudioDevice((byte*)0, 0, &desired, &obtained, 0);
            if (_deviceId == 0)
                throw new InvalidOperationException(
                    $"SDL open audio device failed: {Marshal.PtrToStringUTF8((nint)_sdl.GetError())}");

            _openedSpec = new SampleSpec(SampleFormat.S16Le, obtained.Channels, (uint)obtained.Freq);
            UpdateDevicePausedLocked();
            _logger.LogInformation(
                "{Prefix} opened SDL audio device id={DeviceId} format={Format} channels={Channels} rate={Rate} samples={Samples}",
                PulseServerLogging.Audio, _deviceId, _openedSpec.Format, _openedSpec.Channels, _openedSpec.SampleRate,
                obtained.Samples);
        }
    }

    public void AttachStream(PlaybackStreamState stream, Action playbackProgressCallback)
    {
        lock (_gate)
        {
            _activeStream = stream;
            _playbackProgressCallback = playbackProgressCallback;
            UpdateDevicePausedLocked();
        }
    }

    public void DetachStream(uint channelIndex)
    {
        lock (_gate)
        {
            if (_activeStream?.ChannelIndex != channelIndex)
                return;

            _activeStream = null;
            _playbackProgressCallback = null;
            UpdateDevicePausedLocked();
        }
    }

    public void NotifyStreamStateChanged()
    {
        lock (_gate)
        {
            UpdateDevicePausedLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            UpdateDevicePausedLocked();
        }
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

        bool shouldPause = _activeStream == null || _activeStream.Corked;
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

        PlaybackStreamState? stream;
        Action? callback;
        lock (_gate)
        {
            stream = _activeStream;
            callback = _playbackProgressCallback;
        }

        if (stream == null || stream.Corked)
            return;

        int copied = stream.CopyInto(destination);
        if (callback != null &&
            (copied > 0 || stream.PendingDrainSequence != null || stream.ShouldRequestMore(0)))
        {
            callback();
        }
    }
}
