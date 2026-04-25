using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;
using Xunit;

namespace Podish.Pulse.Tests;

public class DescriptorTests
{
    [Fact]
    public void DescriptorRoundtrip()
    {
        var expected = new Descriptor
        {
            Length = 1024,
            Channel = 1,
            Offset = 0,
            Flags = DescriptorFlags.FlagShmRelease,
        };

        byte[] buffer = new byte[Constants.DescriptorSize];
        DescriptorIO.Write(buffer, expected);

        Descriptor actual = DescriptorIO.Read(buffer);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.Channel, actual.Channel);
        Assert.Equal(expected.Offset, actual.Offset);
        Assert.Equal(expected.Flags, actual.Flags);
    }

    [Fact]
    public void DescriptorEncodeDecode()
    {
        var expected = new Descriptor
        {
            Length = 2048,
            Channel = uint.MaxValue,
            Offset = 100,
            Flags = DescriptorFlags.None,
        };

        byte[] encoded = DescriptorIO.Encode(expected);
        Assert.Equal(Constants.DescriptorSize, encoded.Length);

        Descriptor actual = DescriptorIO.Read(encoded);
        Assert.Equal(expected, actual);
    }
}

public class TagStructTests
{
    [Fact]
    public void U32Serde()
    {
        uint expected = 12345678;
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteU32(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        uint actual = reader.ReadU32();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void U64Serde()
    {
        ulong expected = 12345678901234567890;
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteU64(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        ulong actual = reader.ReadU64();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BoolSerde()
    {
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteBool(true);
        writer.WriteBool(false);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
    }

    [Fact]
    public void StringSerde()
    {
        string expected = "Hello, PulseAudio!";
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteString(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        string? actual = reader.ReadString();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NullStringSerde()
    {
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteNullString();

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        string? actual = reader.ReadString();

        Assert.Null(actual);
    }

    [Fact]
    public void ArbitrarySerde()
    {
        byte[] expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteArbitrary(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        byte[] actual = reader.ReadArbitrary();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TimeValSerde()
    {
        var expected = DateTimeOffset.FromUnixTimeSeconds(1234567890).Add(TimeSpan.FromMicroseconds(123456));
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteTimeVal(expected);

        System.Console.WriteLine($"Buffer: {BitConverter.ToString(writer.Buffer)}");
        System.Console.WriteLine($"Expected: {expected}");

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        DateTimeOffset actual = reader.ReadTimeVal();

        System.Console.WriteLine($"Actual: {actual}");
        Assert.Equal(expected, actual);
    }
}

public class SampleSpecTests
{
    [Fact]
    public void SampleSpecSerde()
    {
        var expected = new SampleSpec(SampleFormat.S16Le, 2, 44100);
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteSampleSpec(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        SampleSpec actual = reader.ReadSampleSpec();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BytesToDuration()
    {
        // 2 bytes per sample, 4 bytes per frame, 48 frames per millisecond.
        var spec = new SampleSpec(SampleFormat.S16Le, 2, 48000);

        Assert.Equal(1000, spec.BytesToDuration(48000 * 4).TotalMilliseconds);
        Assert.Equal(10, spec.BytesToDuration(1920).TotalMilliseconds);
    }
}

public class ChannelMapTests
{
    [Fact]
    public void ChannelMapStereo()
    {
        var expected = ChannelMap.Stereo();
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteChannelMap(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        ChannelMap actual = reader.ReadChannelMap();

        Assert.Equal(2, actual.NumChannels);
        Assert.Equal(ChannelPosition.FrontLeft, actual[0]);
        Assert.Equal(ChannelPosition.FrontRight, actual[1]);
    }

    [Fact]
    public void ChannelMapCustom()
    {
        var expected = new ChannelMap();
        expected.Push(ChannelPosition.FrontLeft);
        expected.Push(ChannelPosition.FrontRight);
        expected.Push(ChannelPosition.RearLeft);
        expected.Push(ChannelPosition.RearRight);

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteChannelMap(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        ChannelMap actual = reader.ReadChannelMap();

        Assert.Equal(expected.NumChannels, actual.NumChannels);
        for (int i = 0; i < expected.NumChannels; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }
}

public class VolumeTests
{
    [Fact]
    public void VolumeSerde()
    {
        Volume expected = Volume.FromLinear(0.5f);
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteVolume(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        Volume actual = reader.ReadVolume();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VolumeConversions()
    {
        Assert.Equal(1.0f, Volume.Normal.ToLinear());
        Assert.Equal(0.0f, Volume.Mute.ToLinear());
        Assert.Equal(0.0, Volume.Normal.ToDb(), 5);
        Assert.Equal(float.NegativeInfinity, Volume.Mute.ToDb());
    }

    [Fact]
    public void ChannelVolumeSerde()
    {
        var expected = ChannelVolume.Norm(2);
        expected[0] = Volume.FromLinear(0.5f);
        expected[1] = Volume.FromLinear(0.75f);

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteChannelVolume(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        ChannelVolume actual = reader.ReadChannelVolume();

        Assert.Equal(expected.Channels, actual.Channels);
        for (int i = 0; i < expected.Channels; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }
}

public class PropsTests
{
    [Fact]
    public void PropsSerde()
    {
        var expected = new Props();
        expected.Set(Prop.ApplicationName, "Test Application");
        expected.Set(Prop.MediaRole, "music");

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteProps(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        Props actual = reader.ReadProps();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.GetString(Prop.ApplicationName), actual.GetString(Prop.ApplicationName));
        Assert.Equal(expected.GetString(Prop.MediaRole), actual.GetString(Prop.MediaRole));
    }
}

public class AuthTests
{
    [Fact]
    public void AuthParamsSerde()
    {
        var expected = new AuthParams(35, true, false, new byte[] { 1, 2, 3, 4 });
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteAuthParams(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        AuthParams actual = reader.ReadAuthParams();

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.SupportsShm, actual.SupportsShm);
        Assert.Equal(expected.SupportsMemfd, actual.SupportsMemfd);
        Assert.Equal(expected.Cookie, actual.Cookie);
    }

    [Fact]
    public void AuthReplySerde()
    {
        var expected = new AuthReply { Version = 35 };
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteAuthReply(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        AuthReply actual = reader.ReadAuthReply();

        Assert.Equal(expected.Version, actual.Version);
    }
}

public class ServerInfoTests
{
    [Fact]
    public void ServerInfoSerde()
    {
        var expected = new ServerInfo
        {
            ServerName = "pulseaudio",
            ServerVersion = "17.0",
            UserName = "tester",
            HostName = "hostbox",
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 44100),
            DefaultSinkName = "sink0",
            DefaultSourceName = "source0",
            Cookie = 0x01020304,
            ChannelMap = ChannelMap.Stereo(),
        };

        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteServerInfo(expected);

        var reader = new TagStructReader(writer.Buffer, Constants.MaxVersion);
        ServerInfo actual = reader.ReadServerInfo();

        Assert.Equal(expected.ServerName, actual.ServerName);
        Assert.Equal(expected.ServerVersion, actual.ServerVersion);
        Assert.Equal(expected.UserName, actual.UserName);
        Assert.Equal(expected.HostName, actual.HostName);
        Assert.Equal(expected.DefaultSinkName, actual.DefaultSinkName);
        Assert.Equal(expected.DefaultSourceName, actual.DefaultSourceName);
        Assert.Equal(expected.Cookie, actual.Cookie);
        Assert.Equal(expected.SampleSpec, actual.SampleSpec);
        Assert.Equal(expected.ChannelMap.NumChannels, actual.ChannelMap.NumChannels);
    }
}

public class ProtocolMessageTests
{
    [Fact]
    public void ProtocolMessageRoundtrip()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var message = new ProtocolMessage(CommandTag.Auth, 1, payload);

        byte[] encoded = ProtocolMessageIO.Encode(message);
        ProtocolMessage decoded = ProtocolMessageIO.Decode(encoded);

        Assert.Equal(message.CommandTag, decoded.CommandTag);
        Assert.Equal(message.Sequence, decoded.Sequence);
        Assert.Equal(message.Payload, decoded.Payload);
    }

    [Fact]
    public void AuthMessageEncodeDecode()
    {
        var authParams = new AuthParams(35, true, false, new byte[] { 1, 2, 3, 4 });
        var message = ProtocolMessage.Create(CommandTag.Auth, 0, writer =>
        {
            writer.WriteAuthParams(authParams);
        });

        byte[] encoded = ProtocolMessageIO.Encode(message);
        ProtocolMessage decoded = ProtocolMessageIO.Decode(encoded);

        Assert.Equal(CommandTag.Auth, decoded.CommandTag);
        Assert.Equal(0u, decoded.Sequence);

        var reader = decoded.ReadPayload();
        AuthParams decodedParams = reader.ReadAuthParams();
        Assert.Equal(authParams.Version, decodedParams.Version);
        Assert.Equal(authParams.SupportsShm, decodedParams.SupportsShm);
    }

    [Fact]
    public void AckEncodeDecode()
    {
        uint expectedSequence = 42;
        byte[] encoded = ProtocolMessageIO.EncodeAck(expectedSequence);

        uint actualSequence = ProtocolMessageIO.DecodeAck(encoded);
        Assert.Equal(expectedSequence, actualSequence);
    }

    [Fact]
    public void ReplyEncodeDecode()
    {
        uint expectedSequence = 100;
        var serverInfo = new ServerInfo
        {
            ServerName = "Test Server",
            ServerVersion = "1.0",
            UserName = "tester",
            HostName = "host",
            SampleSpec = new SampleSpec(SampleFormat.S16Le, 2, 44100),
            DefaultSinkName = "sink0",
            DefaultSourceName = "source0",
            Cookie = 1234,
            ChannelMap = ChannelMap.Stereo(),
        };

        byte[] encoded = ProtocolMessageIO.EncodeReply(expectedSequence, serverInfo, (writer, info) =>
        {
            writer.WriteServerInfo(info);
        });

        var (sequence, decoded) = ProtocolMessageIO.DecodeReply(encoded, reader => reader.ReadServerInfo());
        Assert.Equal(expectedSequence, sequence);
        Assert.Equal(serverInfo.ServerName, decoded.ServerName);
    }
}
