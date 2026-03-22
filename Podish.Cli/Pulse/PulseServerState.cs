using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PulseServerState : IDisposable
{
    private readonly object _gate = new();
    private uint _nextChannelIndex;

    public PulseServerState(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger<PulseServerState>();
        AudioSink = new Sdl3AudioSink(loggerFactory.CreateLogger<Sdl3AudioSink>());
        DefaultSink = CreateDefaultSink(AudioSink.DefaultSampleSpec);
        ServerInfo = CreateServerInfo(DefaultSink);
    }

    public ILoggerFactory LoggerFactory { get; }
    public ILogger Logger { get; }
    public Sdl3AudioSink AudioSink { get; }
    public SinkInfo DefaultSink { get; }
    public ServerInfo ServerInfo { get; }
    public PlaybackStreamState? ActivePlaybackStream { get; private set; }

    public bool TryCreatePlaybackStream(CreatePlaybackStreamParams parameters, string? clientName,
        out PlaybackStreamState? stream, out PulseError? error)
    {
        lock (_gate)
        {
            if (ActivePlaybackStream != null)
            {
                stream = null;
                error = PulseError.Busy;
                return false;
            }

            if (!IsSupported(parameters.SampleSpec))
            {
                stream = null;
                error = PulseError.NotSupported;
                return false;
            }

            stream = new PlaybackStreamState(_nextChannelIndex++, parameters, clientName);
            ActivePlaybackStream = stream;
            error = null;
            return true;
        }
    }

    public PlaybackStreamState? GetPlaybackStream(uint channelIndex)
    {
        lock (_gate)
        {
            if (ActivePlaybackStream?.ChannelIndex == channelIndex)
                return ActivePlaybackStream;
            return null;
        }
    }

    public bool RemovePlaybackStream(uint channelIndex)
    {
        lock (_gate)
        {
            if (ActivePlaybackStream?.ChannelIndex != channelIndex)
                return false;

            ActivePlaybackStream = null;
            return true;
        }
    }

    public static bool IsSupported(SampleSpec sampleSpec)
    {
        return sampleSpec.Format == SampleFormat.S16Le &&
               (sampleSpec.Channels == 1 || sampleSpec.Channels == 2) &&
               (sampleSpec.SampleRate == 44100 || sampleSpec.SampleRate == 48000);
    }

    public void Dispose()
    {
        AudioSink.Dispose();
    }

    private static ServerInfo CreateServerInfo(SinkInfo sink)
    {
        return new ServerInfo
        {
            ServerName = "Podish PulseAudio Server",
            DefaultSinkIndex = sink.Index,
            DefaultSourceIndex = null,
            Cookie = Array.Empty<byte>(),
            DefaultSampleSpec = sink.SampleSpec,
            DefaultChannelMap = sink.ChannelMap,
        };
    }

    private static SinkInfo CreateDefaultSink(SampleSpec sampleSpec)
    {
        var props = new Props();
        props.SetString("device.api", "podish");
        props.SetString("device.description", "Podish SDL Output");
        props.SetString("device.class", "sound");

        return new SinkInfo
        {
            Index = 1,
            Name = "podish.sdl.default",
            Description = "Podish SDL Output",
            SampleSpec = sampleSpec,
            ChannelMap = sampleSpec.Channels == 1 ? ChannelMap.Mono() : ChannelMap.Stereo(),
            MonitorSourceIndex = Constants.InvalidIndex,
            Description2 = "Podish SDL Output",
            Flags = 0,
            Props = props,
            Latency = 0,
            Driver = "podish-sdl",
            Format = sampleSpec.Format,
            Volume = ChannelVolume.Norm(sampleSpec.Channels),
            Mute = false,
            BaseVolume = Volume.Normal,
            State = SinkState.Idle,
            NVolumeSteps = 65536,
            CardIndex = null,
            ActivePortIndex = 0,
            NumInputs = 0,
            NumOutputs = 1,
        };
    }
}
