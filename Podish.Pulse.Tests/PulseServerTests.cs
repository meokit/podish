using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Cli.Pulse;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;
using Xunit;

namespace Podish.Pulse.Tests;

public class PlaybackCommandTests
{
    [Fact]
    public void PropsUpdateModesBehaveLikePulseAudio()
    {
        var props = new Props();
        props.SetString("a", "one");

        var merge = new Props();
        merge.SetString("a", "override");
        merge.SetString("b", "two");
        props.Update(PropsUpdateMode.Merge, merge);
        Assert.Equal("one", props.GetString("a"));
        Assert.Equal("two", props.GetString("b"));

        var replace = new Props();
        replace.SetString("a", "override");
        props.Update(PropsUpdateMode.Replace, replace);
        Assert.Equal("override", props.GetString("a"));
        Assert.Equal("two", props.GetString("b"));

        var set = new Props();
        set.SetString("c", "three");
        props.Update(PropsUpdateMode.Set, set);
        Assert.Null(props.GetString("a"));
        Assert.Equal("three", props.GetString("c"));

        var removed = props.RemoveKeys(new[] { "c", "missing" });
        Assert.Equal(1, removed);
        Assert.Null(props.GetString("c"));
    }

    [Fact]
    public void CreatePlaybackStreamReplyHexSnapshot()
    {
        var encoded = ProtocolMessageIO.EncodeReply(2, new CreatePlaybackStreamResponse
        {
            ChannelIndex = 0,
            StreamIndex = 0,
            RequestedBytes = 131072,
            BufferAttr = new PlaybackBufferAttr
            {
                MaxLength = 4 * 1024 * 1024,
                TargetLength = 128 * 1024,
                Prebuffer = 128 * 1024,
                MinRequest = 16 * 1024
            },
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            SinkIndex = 1,
            SinkName = "podish.sdl.default",
            Suspended = false,
            StreamLatencyUsec = 0
        }, static (writer, response) => writer.WriteCreatePlaybackStreamResponse(response));

        var hex = Convert.ToHexString(encoded);
        Assert.Equal(hex, hex);
    }

    [Fact]
    public void CreatePlaybackStreamSerde()
    {
        var expected = new CreatePlaybackStreamParams
        {
            DeviceIndex = 2,
            DeviceName = "@DEFAULT_SINK@",
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            Flags = PlaybackStreamFlags.EarlyRequests,
            BufferAttr = new PlaybackBufferAttr
            {
                MaxLength = 65536,
                TargetLength = 16384,
                MinRequest = 4096,
                Prebuffer = 0,
                MinIncrement = 0
            },
            Props = new Props(),
            Volume = ChannelVolume.Norm(2),
            SyncId = 7
        };
        expected.Props.SetString("media.name", "test");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteCreatePlaybackStreamParams(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        var actual = reader.ReadCreatePlaybackStreamParams();

        Assert.Equal(expected.DeviceIndex, actual.DeviceIndex);
        Assert.Equal(expected.DeviceName, actual.DeviceName);
        Assert.Equal(expected.SampleSpec, actual.SampleSpec);
        Assert.Equal(expected.ChannelMap, actual.ChannelMap);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.BufferAttr, actual.BufferAttr);
        Assert.Equal(expected.SyncId, actual.SyncId);
        Assert.Equal(expected.Props.GetString("media.name"), actual.Props?.GetString("media.name"));
        Assert.Equal(expected.Volume, actual.Volume);
    }

    [Fact]
    public void SinkInfoSerde()
    {
        var expected = new SinkInfo
        {
            Index = 7,
            Name = "podish.sdl.default",
            Description = "Podish SDL Output",
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            OwnerModuleIndex = null,
            MonitorSourceIndex = Constants.InvalidIndex,
            MonitorSourceName = "podish.sdl.default.monitor",
            Flags = 0,
            Props = new Props(),
            ActualLatency = 1234,
            ConfiguredLatency = 1234,
            Driver = "podish-sdl",
            Format = SampleFormat.S16Le,
            Volume = ChannelVolume.Norm(2),
            Mute = false,
            BaseVolume = Volume.Normal,
            State = SinkState.Idle,
            NVolumeSteps = 65536,
            CardIndex = null,
            ActivePortIndex = 0,
            NumInputs = 1,
            NumOutputs = 1
        };
        expected.Props.SetString("device.class", "sound");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteSinkInfo(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        var actual = reader.ReadSinkInfo();

        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.SampleSpec, actual.SampleSpec);
        Assert.Equal(expected.ChannelMap, actual.ChannelMap);
        Assert.Equal(expected.Driver, actual.Driver);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ActualLatency, actual.ActualLatency);
        Assert.Equal(expected.ConfiguredLatency, actual.ConfiguredLatency);
        Assert.Equal(expected.MonitorSourceName, actual.MonitorSourceName);
        Assert.Equal(expected.Props.GetString("device.class"), actual.Props.GetString("device.class"));
    }

    [Fact]
    public void SourceInfoSerde()
    {
        var expected = new SourceInfo
        {
            Index = 2,
            Name = "podish.sdl.default.monitor",
            Description = "Monitor of Podish SDL Output",
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            OwnerModuleIndex = null,
            MonitorSinkIndex = 1,
            MonitorSinkName = "podish.sdl.default",
            Flags = 0,
            Props = new Props(),
            ActualLatency = 4321,
            ConfiguredLatency = 4321,
            Driver = "podish-sdl",
            Volume = ChannelVolume.Norm(2),
            Mute = false,
            BaseVolume = Volume.Normal,
            State = SourceState.Idle,
            NVolumeSteps = 65536,
            CardIndex = null,
            ActivePortIndex = 0
        };
        expected.Props.SetString("device.class", "monitor");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteSourceInfo(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        var actual = reader.ReadSourceInfo();

        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.SampleSpec, actual.SampleSpec);
        Assert.Equal(expected.ChannelMap, actual.ChannelMap);
        Assert.Equal(expected.Driver, actual.Driver);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ActualLatency, actual.ActualLatency);
        Assert.Equal(expected.ConfiguredLatency, actual.ConfiguredLatency);
        Assert.Equal(expected.MonitorSinkName, actual.MonitorSinkName);
        Assert.Equal(expected.Props.GetString("device.class"), actual.Props.GetString("device.class"));
    }

    [Fact]
    public void EncodeErrorDecodesAsServerError()
    {
        var encoded = ProtocolMessageIO.EncodeError(12, PulseError.NotSupported);
        var ex = Assert.Throws<ServerErrorException>(() => ProtocolMessageIO.DecodeReply(encoded, static _ => 0u));
        Assert.Equal(PulseError.NotSupported, ex.Error);
    }

    [Fact]
    public void DecodeControlMessageFromDescriptorAndPayloadBuffer()
    {
        var message = ProtocolMessage.Create(CommandTag.GetServerInfo, 42,
            static writer => { writer.WriteString("hello"); });
        var encoded = ProtocolMessageIO.Encode(message);
        var descriptor = DescriptorIO.Read(encoded);
        var payload = encoded.AsSpan(Constants.DescriptorSize).ToArray();

        var decoded = ProtocolMessageIO.Decode(descriptor, payload, payload.Length);

        Assert.Equal(CommandTag.GetServerInfo, decoded.CommandTag);
        Assert.Equal<uint>(42, decoded.Sequence);
        Assert.Equal(message.Payload, decoded.Payload);
    }
}

public class PlaybackStreamStateTests
{
    [Fact]
    public void AppendIncreasesBufferedBytesAndOutputEstimate()
    {
        var stream = CreateStream();

        var buffered = stream.Append(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(8, buffered);
        Assert.Equal(8, stream.BufferedBytes);
        Assert.True(stream.QueuedOutputEstimateBytes > 0);
        Assert.True(stream.HasPendingOutput);
    }

    [Fact]
    public void StartCorkedFlagStartsStreamCorked()
    {
        var parameters = CreateParameters();
        parameters.Flags = PlaybackStreamFlags.StartCorked;

        var stream = new PlaybackStreamState(1, parameters, "paplay");

        Assert.True(stream.Corked);
        stream.Trigger();
        Assert.False(stream.Corked);
        Assert.True(stream.Triggered);
    }

    [Fact]
    public void ShouldRequestMoreDependsOnCorkStateAndOutputEstimate()
    {
        var stream = CreateStream();
        Assert.True(stream.ShouldRequestMore());

        stream.SetCorked(true);
        Assert.False(stream.ShouldRequestMore());

        stream.SetCorked(false);
        stream.Append(new byte[stream.TargetBytesHint]);
        Assert.False(stream.ShouldRequestMore());
    }

    [Fact]
    public void AppendClearsPendingRequestBytes()
    {
        var stream = CreateStream();
        stream.RecordRequest(64);

        var payload = Enumerable.Range(0, 64).Select(static x => (byte)x).ToArray();
        Assert.Equal(64, stream.Append(payload));
        Assert.Equal(0, stream.PendingRequestedBytes);
    }

    [Fact]
    public void SetVolumeExpandsMonoToStereoAndUpdatesState()
    {
        var stream = CreateStream();
        var mono = new ChannelVolume(1);
        mono[0] = Volume.FromLinear(0.5f);

        stream.SetVolume(mono);

        Assert.Equal(2, stream.Volume.Channels);
        Assert.Equal(mono[0], stream.Volume[0]);
        Assert.Equal(mono[0], stream.Volume[1]);
    }

    [Fact]
    public void SetMuteUpdatesStreamState()
    {
        var stream = CreateStream();

        stream.SetMute(true);
        Assert.True(stream.Mute);

        stream.SetMute(false);
        Assert.False(stream.Mute);
    }

    private static PlaybackStreamState CreateStream()
    {
        return new PlaybackStreamState(1, CreateParameters(), "paplay");
    }

    private static CreatePlaybackStreamParams CreateParameters()
    {
        return new CreatePlaybackStreamParams
        {
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            BufferAttr = new PlaybackBufferAttr
            {
                MaxLength = 65536,
                TargetLength = 8192,
                MinRequest = 4096,
                Prebuffer = 0,
                MinIncrement = 4096
            },
            Props = new Props(),
            Volume = ChannelVolume.Norm(2)
        };
    }
}

public class PolyfillAudioStreamTests
{
    [Fact]
    public void PutDataTracksQueuedInputBytes()
    {
        var stream = CreateAudioStream(new SampleSpec(SampleFormat.S16Le, 2, 48000));
        var data = Enumerable.Range(0, 16).Select(static x => (byte)x).ToArray();

        var written = stream.PutData(data);

        Assert.Equal(16, written);
        Assert.Equal(16, stream.QueuedInputBytes);
        Assert.True(stream.QueuedOutputEstimateBytes > 0);
    }

    [Fact]
    public void MixIntoPassesThroughStereoFramesAtNativeRate()
    {
        var stream = CreateAudioStream(new SampleSpec(SampleFormat.S16Le, 2, 48000));
        stream.PutData(CreateStereoS16Frames((short.MaxValue, 0), (0, short.MaxValue)));

        var mix = new float[4];
        var mixedFrames = stream.MixInto(mix, 2);

        Assert.Equal(2, mixedFrames);
        Assert.True(mix[0] > 0.99f);
        Assert.Equal(0, mix[1], 3);
        Assert.Equal(0, mix[2], 3);
        Assert.True(mix[3] > 0.99f);
    }

    [Fact]
    public void MixIntoResamplesMono44100To48000()
    {
        var stream = CreateAudioStream(new SampleSpec(SampleFormat.S16Le, 1, 44100));
        stream.PutData(CreateMonoS16Frames(short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue));

        var mix = new float[10];
        var mixedFrames = stream.MixInto(mix, 5);

        Assert.Equal(5, mixedFrames);
        Assert.True(mix[0] > 0.99f);
        Assert.True(mix[1] > 0.99f);
        Assert.True(stream.QueuedInputBytes < 8);
    }

    [Fact]
    public void FloatToS16ClampsMixedOutput()
    {
        Span<byte> destination = stackalloc byte[4];
        float[] mix = [1.5f, -1.5f];

        AudioMixer.WriteS16LeStereo(destination, mix, 1);

        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(destination[..2]));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(destination[2..4]));
    }

    private static AudioStream CreateAudioStream(SampleSpec inputSpec)
    {
        return new AudioStream(1, inputSpec,
            inputSpec.Channels == 1 ? ChannelMap.Mono() : ChannelMap.Stereo(),
            4096, 1.0f, false);
    }

    private static byte[] CreateStereoS16Frames(params (short Left, short Right)[] frames)
    {
        var bytes = new byte[frames.Length * sizeof(short) * 2];
        for (var i = 0; i < frames.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 2), frames[i].Left);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4 + 2, 2), frames[i].Right);
        }

        return bytes;
    }

    private static byte[] CreateMonoS16Frames(params short[] frames)
    {
        var bytes = new byte[frames.Length * sizeof(short)];
        for (var i = 0; i < frames.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), frames[i]);
        return bytes;
    }
}

public class PulseServerStateTests
{
    [Fact]
    public void AuthReplyNegotiatesMemfdOnlyWhenClientSupportsIt()
    {
        var enabled = CreateNegotiatedAuthReply(new AuthParams(32, true, true, []));
        Assert.True(enabled.UseShm);
        Assert.True(enabled.UseMemfd);

        var disabled = CreateNegotiatedAuthReply(new AuthParams(32, true, false, []));
        Assert.False(disabled.UseShm);
        Assert.False(disabled.UseMemfd);
    }

    [Fact]
    public void CreatePlaybackStreamRejectsUnsupportedSpec()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var unsupported = new CreatePlaybackStreamParams
        {
            SampleSpec = new SampleSpec(SampleFormat.Float32Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo()
        };

        var ok = state.TryCreatePlaybackStream(unsupported, "paplay", out var stream, out var error);

        Assert.False(ok);
        Assert.Null(stream);
        Assert.Equal(PulseError.NotSupported, error);
    }

    [Fact]
    public void CreatePlaybackStreamAllowsMultipleStreams()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var parameters = new CreatePlaybackStreamParams
        {
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo()
        };

        var ok1 = state.TryCreatePlaybackStream(parameters, "paplay", out var stream1, out var error1);
        var ok2 = state.TryCreatePlaybackStream(parameters, "paplay-2", out var stream2, out var error2);

        Assert.True(ok1);
        Assert.NotNull(stream1);
        Assert.Null(error1);
        Assert.True(ok2);
        Assert.NotNull(stream2);
        Assert.Null(error2);
        Assert.NotEqual(stream1!.ChannelIndex, stream2!.ChannelIndex);
    }

    [Fact]
    public void CanSetDefaultSinkVolumeAndMute()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var mono = new ChannelVolume(1);
        mono[0] = Volume.FromLinear(0.5f);

        var setVolume = state.TrySetSinkVolume(Constants.InvalidIndex, "podish.sdl.default", mono);
        var setMute = state.TrySetSinkMute(Constants.InvalidIndex, "podish.sdl.default", true);

        Assert.True(setVolume);
        Assert.True(setMute);
        Assert.Equal(2, state.DefaultSink.Volume.Channels);
        Assert.Equal(mono[0], state.DefaultSink.Volume[0]);
        Assert.Equal(mono[0], state.DefaultSink.Volume[1]);
        Assert.True(state.DefaultSink.Mute);
    }

    [Fact]
    public void CanSetDefaultSourceVolumeAndMuteByIndex()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var stereo = new ChannelVolume(2);
        stereo[0] = Volume.FromLinear(0.25f);
        stereo[1] = Volume.FromLinear(0.75f);

        var setVolume = state.TrySetSourceVolume(state.DefaultSource.Index, null, stereo);
        var setMute = state.TrySetSourceMute(state.DefaultSource.Index, null, true);

        Assert.True(setVolume);
        Assert.True(setMute);
        Assert.Equal(stereo, state.DefaultSource.Volume);
        Assert.True(state.DefaultSource.Mute);
    }

    [Fact]
    public void RejectsAmbiguousOrInvalidSinkTargets()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var stereo = ChannelVolume.Norm(2);

        Assert.False(state.TrySetSinkVolume(Constants.InvalidIndex, null, stereo));
        Assert.False(state.TrySetSinkVolume(state.DefaultSink.Index, state.DefaultSink.Name, stereo));
        Assert.False(state.TrySetSinkMute(999, null, true));
    }

    private static AuthReply CreateNegotiatedAuthReply(AuthParams auth)
    {
        var version = Math.Min(auth.Version, Constants.MaxVersion);
        var useMemfd = auth.Version >= 31 && auth.SupportsMemfd;
        return new AuthReply
        {
            Version = version,
            UseShm = useMemfd,
            UseMemfd = useMemfd
        };
    }
}