using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PulseCommandDispatcher
{
    private const ushort MaxNegotiatedClientVersion = 20;
    private readonly ILogger _logger;
    private readonly PulseServerState _state;

    public PulseCommandDispatcher(PulseServerState state, ILogger logger)
    {
        _state = state;
        _logger = logger;
    }

    public async ValueTask DispatchAsync(PulseServerSession session, ProtocolMessage message)
    {
        _logger.LogDebug("{Prefix} seq={Sequence} cmd={Command} payloadLen={PayloadLen}",
            PulseServerLogging.Control, message.Sequence, message.CommandTag, message.Payload.Length);

        try
        {
            switch (message.CommandTag)
            {
                case CommandTag.Auth:
                    await HandleAuthAsync(session, message);
                    return;
                case CommandTag.SetClientName:
                    await HandleSetClientNameAsync(session, message);
                    return;
                case CommandTag.GetServerInfo:
                    await session.SendReplyAsync(message.Sequence, _state.ServerInfo,
                        static (writer, info) => writer.WriteServerInfo(info));
                    return;
                case CommandTag.GetSinkInfo:
                case CommandTag.GetSinkInfoList:
                    await session.SendReplyAsync(message.Sequence, _state.DefaultSink,
                        static (writer, sink) => writer.WriteSinkInfo(sink));
                    return;
                case CommandTag.Stat:
                    await session.SendReplyAsync(message.Sequence, session.CreateStatReply(),
                        static (writer, stat) => writer.WriteStatReply(stat));
                    return;
                case CommandTag.CreatePlaybackStream:
                    await HandleCreatePlaybackStreamAsync(session, message);
                    return;
                case CommandTag.SetPlaybackStreamName:
                    await HandleSetPlaybackStreamNameAsync(session, message);
                    return;
                case CommandTag.CorkPlaybackStream:
                    await HandleCorkPlaybackStreamAsync(session, message);
                    return;
                case CommandTag.FlushPlaybackStream:
                    await HandleFlushPlaybackStreamAsync(session, message);
                    return;
                case CommandTag.TriggerPlaybackStream:
                    await HandleTriggerPlaybackStreamAsync(session, message);
                    return;
                case CommandTag.DrainPlaybackStream:
                    await HandleDrainPlaybackStreamAsync(session, message);
                    return;
                case CommandTag.DeletePlaybackStream:
                    await HandleDeletePlaybackStreamAsync(session, message);
                    return;
                default:
                    _logger.LogWarning("{Prefix} seq={Sequence} unsupported command={Command}",
                        PulseServerLogging.Control, message.Sequence, message.CommandTag);
                    await session.SendErrorAsync(message.Sequence, PulseError.NotImplemented);
                    return;
            }
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning(ex, "{Prefix} seq={Sequence} cmd={Command} protocol error", PulseServerLogging.Control,
                message.Sequence, message.CommandTag);
            await session.SendErrorAsync(message.Sequence, PulseError.Protocol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} seq={Sequence} cmd={Command} internal error", PulseServerLogging.Control,
                message.Sequence, message.CommandTag);
            await session.SendErrorAsync(message.Sequence, PulseError.Internal);
        }
    }

    private static ValueTask HandleAuthAsync(PulseServerSession session, ProtocolMessage message)
    {
        var auth = message.ReadPayload().ReadAuthParams();
        session.ClientProtocolVersion = Math.Min(auth.Version, MaxNegotiatedClientVersion);
        return session.SendReplyAsync(message.Sequence, new AuthReply
        {
            Version = session.ClientProtocolVersion,
            UseShm = false,
            UseMemfd = false,
        }, static (writer, reply) => writer.WriteAuthReply(reply));
    }

    private static ValueTask HandleSetClientNameAsync(PulseServerSession session, ProtocolMessage message)
    {
        var parameters = message.ReadPayload().ReadSetClientNameParams();
        session.ClientProperties = parameters.Properties;
        string? clientName = parameters.Properties.GetString("application.name")
                             ?? parameters.Properties.GetString("application.process.binary")
                             ?? "podish-client";
        session.ClientName = clientName;

        return session.SendReplyAsync(message.Sequence, new SetClientNameReply { ClientIndex = 1 },
            static (writer, reply) => writer.WriteSetClientNameReply(reply));
    }

    private async ValueTask HandleCreatePlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var parameters = message.ReadPayload().ReadCreatePlaybackStreamParams();
        _logger.LogDebug(
            "{Prefix} seq={Sequence} create-playback sampleSpec={Format}/{Channels}/{Rate} deviceIndex={DeviceIndex} deviceName={DeviceName} syncId={SyncId} flags={Flags} bufferAttr=max:{MaxLength} target:{TargetLength} pre:{Prebuffer} minreq:{MinRequest}",
            PulseServerLogging.Control,
            message.Sequence,
            parameters.SampleSpec.Format,
            parameters.SampleSpec.Channels,
            parameters.SampleSpec.SampleRate,
            parameters.DeviceIndex,
            parameters.DeviceName,
            parameters.SyncId,
            parameters.Flags,
            parameters.BufferAttr?.MaxLength,
            parameters.BufferAttr?.TargetLength,
            parameters.BufferAttr?.Prebuffer,
            parameters.BufferAttr?.MinRequest);

        if (!_state.TryCreatePlaybackStream(parameters, session.ClientName, out var stream, out var error))
        {
            await session.SendErrorAsync(message.Sequence, error ?? PulseError.Internal);
            return;
        }

        _state.AudioSink.EnsureFormat(stream!.SampleSpec);
        await session.SendReplyAsync(message.Sequence, new CreatePlaybackStreamResponse
        {
            ChannelIndex = stream.ChannelIndex,
            StreamIndex = stream.StreamIndex,
            RequestedBytes = stream.InitialRequestedBytes,
            BufferAttr = stream.BufferAttr,
            SampleSpec = stream.SampleSpec,
            ChannelMap = stream.ChannelMap,
            SinkIndex = _state.DefaultSink.Index,
            SinkName = _state.DefaultSink.Name,
            Suspended = false,
            StreamLatencyUsec = 0,
        }, static (writer, response) => writer.WriteCreatePlaybackStreamResponse(response));
        _logger.LogDebug(
            "{Prefix} seq={Sequence} create-playback-reply channel={Channel} streamIndex={StreamIndex} requested={Requested} sinkIndex={SinkIndex} sinkName={SinkName} bufferAttr=max:{MaxLength} target:{TargetLength} pre:{Prebuffer} minreq:{MinRequest}",
            PulseServerLogging.Control,
            message.Sequence,
            stream.ChannelIndex,
            stream.StreamIndex,
            stream.InitialRequestedBytes,
            _state.DefaultSink.Index,
            _state.DefaultSink.Name,
            stream.BufferAttr.MaxLength,
            stream.BufferAttr.TargetLength,
            stream.BufferAttr.Prebuffer,
            stream.BufferAttr.MinRequest);
        session.AttachPlaybackStream(stream);
    }

    private async ValueTask HandleSetPlaybackStreamNameAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? channelIndex = reader.ReadIndex();
        string? streamName = reader.ReadString();
        if (channelIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        var stream = _state.GetPlaybackStream(channelIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        stream.SetStreamName(streamName);
        await session.SendAckAsync(message.Sequence);
    }

    private async ValueTask HandleCorkPlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? channelIndex = reader.ReadIndex();
        bool cork = reader.ReadBool();
        if (channelIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        var stream = _state.GetPlaybackStream(channelIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        stream.SetCorked(cork);
        session.NotifyPlaybackStateChanged();
        await session.SendAckAsync(message.Sequence);
        if (!cork)
            await session.MaybeSendPlaybackRequestAsync(stream);
    }

    private async ValueTask HandleFlushPlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var stream = await ResolveStreamByIndexAsync(session, message);
        if (stream == null)
            return;

        stream.Clear();
        session.NotifyPlaybackStateChanged();
        await session.SendAckAsync(message.Sequence);
    }

    private async ValueTask HandleTriggerPlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var stream = await ResolveStreamByIndexAsync(session, message);
        if (stream == null)
            return;

        stream.Trigger();
        session.NotifyPlaybackStateChanged();
        await session.SendAckAsync(message.Sequence);
        await session.MaybeSendPlaybackRequestAsync(stream);
    }

    private async ValueTask HandleDrainPlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var stream = await ResolveStreamByIndexAsync(session, message);
        if (stream == null)
            return;

        stream.QueueDrain(message.Sequence);
        await session.TryCompleteDrainAsync(stream);
    }

    private async ValueTask HandleDeletePlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var stream = await ResolveStreamByIndexAsync(session, message);
        if (stream == null)
            return;

        stream.Clear();
        _state.RemovePlaybackStream(stream.ChannelIndex);
        session.DetachPlaybackStream(stream.ChannelIndex);
        session.NotifyPlaybackStateChanged();
        await session.SendAckAsync(message.Sequence);
    }

    private async ValueTask<PlaybackStreamState?> ResolveStreamByIndexAsync(PulseServerSession session,
        ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? channelIndex = reader.ReadIndex();
        if (channelIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return null;
        }

        var stream = _state.GetPlaybackStream(channelIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return null;
        }

        return stream;
    }
}
