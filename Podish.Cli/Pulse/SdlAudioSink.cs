using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Pulse.Audio;
using Podish.Pulse.Protocol;
using Silk.NET.SDL;

namespace Podish.Cli.Pulse;

internal sealed unsafe class SdlAudioSink : ManagedAudioSink
{
    private readonly ILogger _logger;
    private readonly Sdl _sdl;
    private readonly AudioCallback _audioCallback;
    private GCHandle _selfHandle;
    private bool _initialized;
    private uint _deviceId;

    public SdlAudioSink(ILogger logger) : base(new SampleSpec(SampleFormat.S16Le, 2, 48000))
    {
        _logger = logger;
        _sdl = Sdl.GetApi();
        _audioCallback = OnAudioCallback;
    }

    protected override void EnsureBackendFormatCore(SampleSpec sampleSpec)
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
                PulseServerLogging.Audio, _deviceId, sampleSpec.Format, sampleSpec.Channels,
                sampleSpec.SampleRate, obtained.Samples);
        }
    }

    protected override void SetBackendPausedCore(bool paused)
    {
        if (_deviceId == 0)
            return;

        _sdl.PauseAudioDevice(_deviceId, paused ? 1 : 0);
    }

    protected override void DisposeCore()
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

    private static void OnAudioCallback(void* userdata, byte* stream, int len)
    {
        if (userdata == null || stream == null || len <= 0)
            return;

        var handle = GCHandle.FromIntPtr((nint)userdata);
        if (handle.Target is not SdlAudioSink sink)
            return;

        sink.RenderAudio(new Span<byte>(stream, len));
    }
}
