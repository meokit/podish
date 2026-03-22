using Microsoft.Extensions.Logging.Abstractions;
using Podish.Cli.Pulse;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;
using Xunit;

namespace Podish.Pulse.Tests;

public class PlaybackCommandTests
{
    [Fact]
    public void CreatePlaybackStreamReplyHexSnapshot()
    {
        byte[] encoded = ProtocolMessageIO.EncodeReply(2, new CreatePlaybackStreamResponse
        {
            ChannelIndex = 0,
            StreamIndex = 0,
            RequestedBytes = 131072,
            BufferAttr = new PlaybackBufferAttr
            {
                MaxLength = 4 * 1024 * 1024,
                TargetLength = 128 * 1024,
                Prebuffer = 128 * 1024,
                MinRequest = 16 * 1024,
            },
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
            SinkIndex = 1,
            SinkName = "podish.sdl.default",
            Suspended = false,
            StreamLatencyUsec = 0,
        }, static (writer, response) => writer.WriteCreatePlaybackStreamResponse(response));

        string hex = Convert.ToHexString(encoded);
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
                MinIncrement = 0,
            },
            Props = new Props(),
            Volume = ChannelVolume.Norm(2),
            SyncId = 7,
        };
        expected.Props.SetString("media.name", "test");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteCreatePlaybackStreamParams(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        CreatePlaybackStreamParams actual = reader.ReadCreatePlaybackStreamParams();

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
            MonitorSourceIndex = Constants.InvalidIndex,
            Description2 = "Podish SDL Output",
            Flags = 0,
            Props = new Props(),
            Latency = 1234,
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
            NumOutputs = 1,
        };
        expected.Props.SetString("device.class", "sound");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteSinkInfo(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        SinkInfo actual = reader.ReadSinkInfo();

        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.SampleSpec, actual.SampleSpec);
        Assert.Equal(expected.ChannelMap, actual.ChannelMap);
        Assert.Equal(expected.Driver, actual.Driver);
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.Props.GetString("device.class"), actual.Props.GetString("device.class"));
    }

    [Fact]
    public void EncodeErrorDecodesAsServerError()
    {
        byte[] encoded = ProtocolMessageIO.EncodeError(12, PulseError.NotSupported);
        var ex = Assert.Throws<ServerErrorException>(() => ProtocolMessageIO.DecodeReply(encoded, static _ => 0u));
        Assert.Equal(PulseError.NotSupported, ex.Error);
    }

    [Fact]
    public void DecodeControlMessageFromDescriptorAndPayloadBuffer()
    {
        var message = ProtocolMessage.Create(CommandTag.GetServerInfo, 42, static writer =>
        {
            writer.WriteString("hello");
        });
        byte[] encoded = ProtocolMessageIO.Encode(message);
        Descriptor descriptor = DescriptorIO.Read(encoded);
        byte[] payload = encoded.AsSpan(Constants.DescriptorSize).ToArray();

        ProtocolMessage decoded = ProtocolMessageIO.Decode(descriptor, payload, payload.Length);

        Assert.Equal(CommandTag.GetServerInfo, decoded.CommandTag);
        Assert.Equal<uint>(42, decoded.Sequence);
        Assert.Equal(message.Payload, decoded.Payload);
    }
}

public class PlaybackStreamStateTests
{
    [Fact]
    public void AppendAndCopyIntoConsumesBufferedBytes()
    {
        var stream = CreateStream();

        int buffered = stream.Append(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(5, buffered);
        Assert.Equal(5, stream.BufferedBytes);

        Span<byte> destination = stackalloc byte[3];
        int copied = stream.CopyInto(destination);

        Assert.Equal(3, copied);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
        Assert.Equal(2, stream.BufferedBytes);
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
    public void ShouldRequestMoreDependsOnCorkStateAndQueuedBytes()
    {
        var stream = CreateStream();
        Assert.True(stream.ShouldRequestMore(0));

        stream.SetCorked(true);
        Assert.False(stream.ShouldRequestMore(0));

        stream.SetCorked(false);
        stream.Append(new byte[stream.TargetBytesHint]);
        Assert.False(stream.ShouldRequestMore(0));
    }

    [Fact]
    public void AppendAndCopyIntoWrapsRingBufferAndClearsPendingRequestBytes()
    {
        var stream = CreateStream();
        stream.RecordRequest(64);

        byte[] first = Enumerable.Range(0, 48).Select(static x => (byte)x).ToArray();
        byte[] second = Enumerable.Range(48, 32).Select(static x => (byte)x).ToArray();

        Assert.Equal(48, stream.Append(first));

        Span<byte> prefix = stackalloc byte[32];
        Assert.Equal(32, stream.CopyInto(prefix));
        Assert.Equal(first.AsSpan(0, 32).ToArray(), prefix.ToArray());

        Assert.Equal(48, stream.Append(second));
        Assert.Equal(0, stream.PendingRequestedBytes);

        byte[] drained = new byte[48];
        Assert.Equal(48, stream.CopyInto(drained));
        Assert.Equal(first.AsSpan(32, 16).ToArray().Concat(second).ToArray(), drained);
        Assert.Equal(0, stream.BufferedBytes);
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
                MinIncrement = 4096,
            },
            Props = new Props(),
            Volume = ChannelVolume.Norm(2),
        };
    }
}

public class PulseServerStateTests
{
    [Fact]
    public void CreatePlaybackStreamRejectsUnsupportedSpec()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var unsupported = new CreatePlaybackStreamParams
        {
            SampleSpec = new SampleSpec(SampleFormat.Float32Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
        };

        bool ok = state.TryCreatePlaybackStream(unsupported, "paplay", out var stream, out var error);

        Assert.False(ok);
        Assert.Null(stream);
        Assert.Equal(PulseError.NotSupported, error);
    }

    [Fact]
    public void CreatePlaybackStreamAllowsOnlyOneActiveStream()
    {
        using var state = new PulseServerState(NullLoggerFactory.Instance);
        var parameters = new CreatePlaybackStreamParams
        {
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 48000),
            ChannelMap = ChannelMap.Stereo(),
        };

        bool ok1 = state.TryCreatePlaybackStream(parameters, "paplay", out var stream1, out var error1);
        bool ok2 = state.TryCreatePlaybackStream(parameters, "paplay-2", out var stream2, out var error2);

        Assert.True(ok1);
        Assert.NotNull(stream1);
        Assert.Null(error1);
        Assert.False(ok2);
        Assert.Null(stream2);
        Assert.Equal(PulseError.Busy, error2);
    }
}
