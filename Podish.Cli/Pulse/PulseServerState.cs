using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PulseServerState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, PlaybackStreamState> _playbackStreams = new();
    private uint _nextChannelIndex;

    public PulseServerState(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger<PulseServerState>();
        AudioSink = new SdlAudioSink(loggerFactory.CreateLogger<SdlAudioSink>());
        DefaultSink = CreateDefaultSink(AudioSink.DefaultSampleSpec);
        DefaultSource = CreateDefaultSource(DefaultSink);
        ServerInfo = CreateServerInfo(DefaultSink);
        AudioSink.SetMasterVolume(DefaultSink.Volume, DefaultSink.Mute);
    }

    public ILoggerFactory LoggerFactory { get; }
    public ILogger Logger { get; }
    public SdlAudioSink AudioSink { get; }
    public SinkInfo DefaultSink { get; }
    public SourceInfo DefaultSource { get; }
    public ServerInfo ServerInfo { get; }

    public void Dispose()
    {
        AudioSink.Dispose();
    }

    public bool TryCreatePlaybackStream(CreatePlaybackStreamParams parameters, string? clientName,
        out PlaybackStreamState? stream, out PulseError? error)
    {
        lock (_gate)
        {
            if (!IsSupported(parameters.SampleSpec))
            {
                stream = null;
                error = PulseError.NotSupported;
                return false;
            }

            stream = new PlaybackStreamState(_nextChannelIndex++, parameters, clientName);
            _playbackStreams.Add(stream.ChannelIndex, stream);
            error = null;
            return true;
        }
    }

    public PlaybackStreamState? GetPlaybackStream(uint channelIndex)
    {
        lock (_gate)
        {
            _playbackStreams.TryGetValue(channelIndex, out var stream);
            return stream;
        }
    }

    public bool RemovePlaybackStream(uint channelIndex)
    {
        lock (_gate)
        {
            return _playbackStreams.Remove(channelIndex);
        }
    }

    public PlaybackStreamState[] GetPlaybackStreamsSnapshot()
    {
        lock (_gate)
        {
            return _playbackStreams.Values.OrderBy(static stream => stream.StreamIndex).ToArray();
        }
    }

    public PlaybackStreamState? GetPlaybackStreamByStreamIndex(uint streamIndex)
    {
        lock (_gate)
        {
            return _playbackStreams.Values.FirstOrDefault(stream => stream.StreamIndex == streamIndex);
        }
    }

    public bool TryLookupSink(string? name, out uint index)
    {
        lock (_gate)
        {
            if (MatchesSinkName(name, DefaultSink.Name, ServerInfo.DefaultSinkName))
            {
                index = DefaultSink.Index;
                return true;
            }

            index = 0;
            return false;
        }
    }

    public bool TryLookupSource(string? name, out uint index)
    {
        lock (_gate)
        {
            if (MatchesSourceName(name, DefaultSource.Name, ServerInfo.DefaultSourceName))
            {
                index = DefaultSource.Index;
                return true;
            }

            index = 0;
            return false;
        }
    }

    public bool TrySetDefaultSink(string? name)
    {
        lock (_gate)
        {
            if (name == "@NONE@")
            {
                ServerInfo.DefaultSinkName = null;
                return true;
            }

            if (!MatchesSinkName(name, DefaultSink.Name, ServerInfo.DefaultSinkName))
                return false;

            ServerInfo.DefaultSinkName = DefaultSink.Name;
            return true;
        }
    }

    public bool TrySetDefaultSource(string? name)
    {
        lock (_gate)
        {
            if (name == "@NONE@")
            {
                ServerInfo.DefaultSourceName = null;
                return true;
            }

            if (!MatchesSourceName(name, DefaultSource.Name, ServerInfo.DefaultSourceName))
                return false;

            ServerInfo.DefaultSourceName = DefaultSource.Name;
            return true;
        }
    }

    public bool TrySetSinkVolume(uint? index, string? name, ChannelVolume volume)
    {
        lock (_gate)
        {
            if (!TryResolveSinkTarget(index, name))
                return false;

            DefaultSink.Volume = NormalizeChannelVolume(volume, DefaultSink.SampleSpec.Channels);
            AudioSink.SetMasterVolume(DefaultSink.Volume, DefaultSink.Mute);
            return true;
        }
    }

    public bool TrySetSourceVolume(uint? index, string? name, ChannelVolume volume)
    {
        lock (_gate)
        {
            if (!TryResolveSourceTarget(index, name))
                return false;

            DefaultSource.Volume = NormalizeChannelVolume(volume, DefaultSource.SampleSpec.Channels);
            return true;
        }
    }

    public bool TrySetSinkMute(uint? index, string? name, bool mute)
    {
        lock (_gate)
        {
            if (!TryResolveSinkTarget(index, name))
                return false;

            DefaultSink.Mute = mute;
            AudioSink.SetMasterVolume(DefaultSink.Volume, DefaultSink.Mute);
            return true;
        }
    }

    public bool TrySetSourceMute(uint? index, string? name, bool mute)
    {
        lock (_gate)
        {
            if (!TryResolveSourceTarget(index, name))
                return false;

            DefaultSource.Mute = mute;
            return true;
        }
    }

    public static bool IsSupported(SampleSpec sampleSpec)
    {
        return sampleSpec.Format == SampleFormat.S16Le &&
               (sampleSpec.Channels == 1 || sampleSpec.Channels == 2) &&
               sampleSpec.SampleRate > 0;
    }

    private static bool MatchesSinkName(string? requestedName, string sinkName, string? defaultSinkName)
    {
        return requestedName == Constants.DefaultSink ||
               requestedName == sinkName ||
               (!string.IsNullOrEmpty(defaultSinkName) && requestedName == defaultSinkName);
    }

    private static bool MatchesSourceName(string? requestedName, string sourceName, string? defaultSourceName)
    {
        return requestedName == Constants.DefaultSource ||
               requestedName == sourceName ||
               (!string.IsNullOrEmpty(defaultSourceName) && requestedName == defaultSourceName);
    }

    private static ServerInfo CreateServerInfo(SinkInfo sink)
    {
        return new ServerInfo
        {
            ServerName = "pulseaudio",
            ServerVersion = "17.0",
            UserName = "podish",
            HostName = Environment.MachineName,
            SampleSpec = sink.SampleSpec,
            DefaultSinkName = sink.Name,
            DefaultSourceName = $"{sink.Name}.monitor",
            Cookie = 0x504F4449,
            ChannelMap = sink.ChannelMap ?? new ChannelMap()
        };
    }

    private static SinkInfo CreateDefaultSink(SampleSpec sampleSpec)
    {
        var props = new Props();
        props.SetString("device.api", "podish");
        props.SetString("device.description", "Podish SDL Output");
        props.SetString("device.class", "sound");
        props.SetString("device.string", "podish-sdl");

        var ports = new List<PortInfo>
        {
            new()
            {
                Name = "analog-output",
                Description = "Analog Output",
                Priority = 0
            }
        };

        return new SinkInfo
        {
            Index = 1,
            Name = "podish.sdl.default",
            Description = "Podish SDL Output",
            SampleSpec = sampleSpec,
            ChannelMap = sampleSpec.Channels == 1 ? ChannelMap.Mono() : ChannelMap.Stereo(),
            OwnerModuleIndex = null,
            MonitorSourceIndex = 2,
            MonitorSourceName = "podish.sdl.default.monitor",
            Flags = 0,
            Props = props,
            ActualLatency = 0,
            ConfiguredLatency = 0,
            Driver = "podish-sdl",
            Format = sampleSpec.Format,
            Volume = ChannelVolume.Norm(sampleSpec.Channels),
            Mute = false,
            BaseVolume = Volume.Normal,
            State = SinkState.Idle,
            NVolumeSteps = 65536,
            CardIndex = null,
            Ports = ports,
            ActivePortIndex = 0,
            NumInputs = 0,
            NumOutputs = 1
        };
    }

    private static SourceInfo CreateDefaultSource(SinkInfo sink)
    {
        var props = new Props();
        props.SetString("device.api", "podish");
        props.SetString("device.description", "Monitor of Podish SDL Output");
        props.SetString("device.class", "monitor");
        props.SetString("device.string", "podish-sdl.monitor");

        return new SourceInfo
        {
            Index = sink.MonitorSourceIndex,
            Name = sink.MonitorSourceName ?? $"{sink.Name}.monitor",
            Description = "Monitor of Podish SDL Output",
            SampleSpec = sink.SampleSpec,
            ChannelMap = sink.ChannelMap ?? new ChannelMap(),
            OwnerModuleIndex = null,
            MonitorSinkIndex = sink.Index,
            MonitorSinkName = sink.Name,
            Flags = 0,
            Props = props,
            ActualLatency = 0,
            ConfiguredLatency = 0,
            Driver = "podish-sdl",
            Volume = ChannelVolume.Norm(sink.SampleSpec.Channels),
            Mute = false,
            BaseVolume = Volume.Normal,
            State = SourceState.Idle,
            NVolumeSteps = 65536,
            CardIndex = null,
            Ports = new List<PortInfo>(),
            ActivePortIndex = 0
        };
    }

    private bool TryResolveSinkTarget(uint? index, string? name)
    {
        var hasIndex = index is not null && index.Value != Constants.InvalidIndex;
        var hasName = !string.IsNullOrEmpty(name);
        if (hasIndex == hasName)
            return false;

        return hasIndex
            ? index == DefaultSink.Index
            : MatchesSinkName(name, DefaultSink.Name, ServerInfo.DefaultSinkName);
    }

    private bool TryResolveSourceTarget(uint? index, string? name)
    {
        var hasIndex = index is not null && index.Value != Constants.InvalidIndex;
        var hasName = !string.IsNullOrEmpty(name);
        if (hasIndex == hasName)
            return false;

        return hasIndex
            ? index == DefaultSource.Index
            : MatchesSourceName(name, DefaultSource.Name, ServerInfo.DefaultSourceName);
    }

    private static ChannelVolume NormalizeChannelVolume(ChannelVolume requested, byte expectedChannels)
    {
        if (requested.Channels == expectedChannels)
            return CloneChannelVolume(requested);

        if (requested.Channels == 1)
        {
            var expanded = new ChannelVolume();
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
        var clone = new ChannelVolume();
        for (var i = 0; i < requested.Channels; i++)
            clone.Push(requested[i]);
        return clone;
    }
}