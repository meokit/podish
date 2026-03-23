using Microsoft.Extensions.Logging;
using Podish.Pulse.Audio;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PulseCommandDispatcher
{
    private const ushort MaxNegotiatedClientVersion = Constants.MaxVersion;
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
                case CommandTag.RegisterMemfdShmid:
                    await HandleRegisterMemfdShmidAsync(session, message);
                    return;
                case CommandTag.GetServerInfo:
                    await session.SendReplyAsync(message.Sequence, _state.ServerInfo,
                        static (writer, info) => writer.WriteServerInfo(info),
                        SummarizeServerInfo(_state.ServerInfo));
                    return;
                case CommandTag.GetSinkInfo:
                case CommandTag.GetSinkInfoList:
                    LogSinkInfoRequest(message);
                    await session.SendReplyAsync(message.Sequence, _state.DefaultSink,
                        static (writer, sink) => writer.WriteSinkInfo(sink),
                        SummarizeSinkInfo(_state.DefaultSink));
                    return;
                case CommandTag.GetSourceInfo:
                case CommandTag.GetSourceInfoList:
                    LogSourceInfoRequest(message);
                    await session.SendReplyAsync(message.Sequence, _state.DefaultSource,
                        static (writer, source) => writer.WriteSourceInfo(source),
                        SummarizeSourceInfo(_state.DefaultSource));
                    return;
                case CommandTag.GetClientInfo:
                case CommandTag.GetClientInfoList:
                    await HandleClientInfoAsync(session, message);
                    return;
                case CommandTag.GetSinkInputInfo:
                case CommandTag.GetSinkInputInfoList:
                    await HandleSinkInputInfoAsync(session, message);
                    return;
                case CommandTag.GetSourceOutputInfo:
                case CommandTag.GetSourceOutputInfoList:
                    await HandleSourceOutputInfoAsync(session, message);
                    return;
                case CommandTag.Subscribe:
                    await HandleSubscribeAsync(session, message);
                    return;
                case CommandTag.LookupSink:
                    await HandleLookupSinkAsync(session, message);
                    return;
                case CommandTag.LookupSource:
                    await HandleLookupSourceAsync(session, message);
                    return;
                case CommandTag.SetDefaultSink:
                    await HandleSetDefaultSinkAsync(session, message);
                    return;
                case CommandTag.SetDefaultSource:
                    await HandleSetDefaultSourceAsync(session, message);
                    return;
                case CommandTag.SetSinkVolume:
                    await HandleSetSinkVolumeAsync(session, message);
                    return;
                case CommandTag.SetSourceVolume:
                    await HandleSetSourceVolumeAsync(session, message);
                    return;
                case CommandTag.SetSinkMute:
                    await HandleSetSinkMuteAsync(session, message);
                    return;
                case CommandTag.SetSourceMute:
                    await HandleSetSourceMuteAsync(session, message);
                    return;
                case CommandTag.SetSinkInputVolume:
                    await HandleSetSinkInputVolumeAsync(session, message);
                    return;
                case CommandTag.SetSinkInputMute:
                    await HandleSetSinkInputMuteAsync(session, message);
                    return;
                case CommandTag.SetSourceOutputVolume:
                    await HandleSetSourceOutputVolumeAsync(session, message);
                    return;
                case CommandTag.SetSourceOutputMute:
                    await HandleSetSourceOutputMuteAsync(session, message);
                    return;
                case CommandTag.UpdateClientProplist:
                    await HandleUpdateClientProplistAsync(session, message);
                    return;
                case CommandTag.UpdatePlaybackStreamProplist:
                    await HandleUpdatePlaybackStreamProplistAsync(session, message);
                    return;
                case CommandTag.RemoveClientProplist:
                    await HandleRemoveClientProplistAsync(session, message);
                    return;
                case CommandTag.RemovePlaybackStreamProplist:
                    await HandleRemovePlaybackStreamProplistAsync(session, message);
                    return;
                case CommandTag.Stat:
                    var stat = session.CreateStatReply();
                    await session.SendReplyAsync(message.Sequence, stat,
                        static (writer, stat) => writer.WriteStatReply(stat),
                        SummarizeStatReply(stat));
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
        bool useMemfd = auth.Version >= 31 && auth.SupportsMemfd;
        return session.SendReplyAsync(message.Sequence, new AuthReply
        {
            Version = session.ClientProtocolVersion,
            UseShm = useMemfd,
            UseMemfd = useMemfd,
        }, static (writer, reply) => writer.WriteAuthReply(reply),
            $"auth version={session.ClientProtocolVersion} useShm={useMemfd} useMemfd={useMemfd}");
    }

    private ValueTask HandleSetClientNameAsync(PulseServerSession session, ProtocolMessage message)
    {
        var parameters = message.ReadPayload().ReadSetClientNameParams();
        session.ClientProperties = parameters.Properties;
        string? clientName = parameters.Properties.GetString("application.name")
                             ?? parameters.Properties.GetString("application.process.binary")
                             ?? "podish-client";
        session.ClientName = clientName;
        _logger.LogDebug(
            "{Prefix} seq={Sequence} client-name name={ClientName} binary={Binary} mediaName={MediaName}",
            PulseServerLogging.Control,
            message.Sequence,
            clientName,
            parameters.Properties.GetString("application.process.binary"),
            parameters.Properties.GetString("media.name"));

        return session.SendReplyAsync(message.Sequence, new SetClientNameReply { ClientIndex = 1 },
            static (writer, reply) => writer.WriteSetClientNameReply(reply),
            $"set-client-name clientIndex=1 name={clientName}");
    }

    private async ValueTask HandleRegisterMemfdShmidAsync(PulseServerSession session, ProtocolMessage message)
    {
        uint shmId = message.ReadPayload().ReadU32();
        _logger.LogDebug("{Prefix} seq={Sequence} register-memfd-shmid shmId={ShmId}",
            PulseServerLogging.Control, message.Sequence, shmId);

        if (!session.TryRegisterMemfdShmid(shmId))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        await session.SendAckAsync(message.Sequence, $"register-memfd-shmid shmId={shmId}");
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
        }, static (writer, response) => writer.WriteCreatePlaybackStreamResponse(response),
            SummarizeCreatePlaybackStreamReply(stream, _state.DefaultSink));
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
        await session.SendAckAsync(message.Sequence,
            $"set-playback-stream-name channel={channelIndex.Value} name={streamName}");
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
        await session.SendAckAsync(message.Sequence,
            $"cork-playback-stream channel={channelIndex.Value} cork={cork}");
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
        await session.SendAckAsync(message.Sequence,
            $"flush-playback-stream channel={stream.ChannelIndex}");
    }

    private async ValueTask HandleTriggerPlaybackStreamAsync(PulseServerSession session, ProtocolMessage message)
    {
        var stream = await ResolveStreamByIndexAsync(session, message);
        if (stream == null)
            return;

        stream.Trigger();
        session.NotifyPlaybackStateChanged();
        await session.SendAckAsync(message.Sequence,
            $"trigger-playback-stream channel={stream.ChannelIndex}");
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
        await session.SendAckAsync(message.Sequence,
            $"delete-playback-stream channel={stream.ChannelIndex}");
    }

    private async ValueTask HandleClientInfoAsync(PulseServerSession session, ProtocolMessage message)
    {
        ClientInfo client = CreateClientInfo(session);

        if (message.CommandTag == CommandTag.GetClientInfo)
        {
            uint? clientIndex = message.ReadPayload().ReadIndex();
            _logger.LogDebug("{Prefix} seq={Sequence} get-client-info index={ClientIndex}",
                PulseServerLogging.Control, message.Sequence, clientIndex);

            if (clientIndex is not 1)
            {
                await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
                return;
            }
        }
        else
        {
            _logger.LogDebug("{Prefix} seq={Sequence} get-client-info-list",
                PulseServerLogging.Control, message.Sequence);
        }

        await session.SendReplyAsync(message.Sequence, client,
            static (writer, info) => writer.WriteClientInfo(info),
            SummarizeClientInfo(client));
    }

    private async ValueTask HandleSinkInputInfoAsync(PulseServerSession session, ProtocolMessage message)
    {
        PlaybackStreamState[] streams = _state.GetPlaybackStreamsSnapshot();

        if (message.CommandTag == CommandTag.GetSinkInputInfo)
        {
            uint? sinkInputIndex = message.ReadPayload().ReadIndex();
            _logger.LogDebug("{Prefix} seq={Sequence} get-sink-input-info index={SinkInputIndex}",
                PulseServerLogging.Control, message.Sequence, sinkInputIndex);

            PlaybackStreamState? stream = sinkInputIndex is null
                ? null
                : streams.FirstOrDefault(candidate => candidate.StreamIndex == sinkInputIndex.Value);

            if (stream == null)
            {
                await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
                return;
            }

            SinkInputInfo info = CreateSinkInputInfo(stream);
            await session.SendReplyAsync(message.Sequence, info,
                static (writer, item) => writer.WriteSinkInputInfo(item),
                SummarizeSinkInputInfo(info));
            return;
        }

        _logger.LogDebug("{Prefix} seq={Sequence} get-sink-input-info-list count={Count}",
            PulseServerLogging.Control, message.Sequence, streams.Length);

        if (streams.Length == 0)
        {
            await session.SendAckAsync(message.Sequence, "sink-input-info-list empty");
            return;
        }

        await session.SendReplyAsync(message.Sequence, streams,
            static (writer, items) =>
            {
                foreach (PlaybackStreamState stream in items)
                    writer.WriteSinkInputInfo(CreateSinkInputInfo(stream));
            },
            $"sink-input-info-list count={streams.Length}");
    }

    private async ValueTask HandleSourceOutputInfoAsync(PulseServerSession session, ProtocolMessage message)
    {
        if (message.CommandTag == CommandTag.GetSourceOutputInfo)
        {
            uint? sourceOutputIndex = message.ReadPayload().ReadIndex();
            _logger.LogDebug("{Prefix} seq={Sequence} get-source-output-info index={SourceOutputIndex}",
                PulseServerLogging.Control, message.Sequence, sourceOutputIndex);
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        _logger.LogDebug("{Prefix} seq={Sequence} get-source-output-info-list count=0",
            PulseServerLogging.Control, message.Sequence);
        await session.SendAckAsync(message.Sequence, "source-output-info-list empty");
    }

    private async ValueTask HandleSubscribeAsync(PulseServerSession session, ProtocolMessage message)
    {
        uint mask = message.ReadPayload().ReadU32();
        _logger.LogDebug("{Prefix} seq={Sequence} subscribe mask=0x{Mask:x8}",
            PulseServerLogging.Control, message.Sequence, mask);
        await session.SendAckAsync(message.Sequence, $"subscribe mask=0x{mask:x8}");
    }

    private async ValueTask HandleLookupSinkAsync(PulseServerSession session, ProtocolMessage message)
    {
        string? name = message.ReadPayload().ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} lookup-sink name={Name}",
            PulseServerLogging.Control, message.Sequence, name);

        if (!_state.TryLookupSink(name, out uint index))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendReplyAsync(message.Sequence, index,
            static (writer, value) => writer.WriteU32(value),
            $"lookup-sink index={index} name={name}");
    }

    private async ValueTask HandleLookupSourceAsync(PulseServerSession session, ProtocolMessage message)
    {
        string? name = message.ReadPayload().ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} lookup-source name={Name}",
            PulseServerLogging.Control, message.Sequence, name);

        if (!_state.TryLookupSource(name, out uint index))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendReplyAsync(message.Sequence, index,
            static (writer, value) => writer.WriteU32(value),
            $"lookup-source index={index} name={name}");
    }

    private async ValueTask HandleSetDefaultSinkAsync(PulseServerSession session, ProtocolMessage message)
    {
        string? name = message.ReadPayload().ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} set-default-sink name={Name}",
            PulseServerLogging.Control, message.Sequence, name);

        if (!_state.TrySetDefaultSink(name))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence, $"set-default-sink name={name}");
    }

    private async ValueTask HandleSetDefaultSourceAsync(PulseServerSession session, ProtocolMessage message)
    {
        string? name = message.ReadPayload().ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} set-default-source name={Name}",
            PulseServerLogging.Control, message.Sequence, name);

        if (!_state.TrySetDefaultSource(name))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence, $"set-default-source name={name}");
    }

    private async ValueTask HandleSetSinkVolumeAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? index = reader.ReadIndex();
        string? name = reader.ReadString();
        ChannelVolume volume = reader.ReadChannelVolume();
        _logger.LogDebug("{Prefix} seq={Sequence} set-sink-volume index={Index} name={Name} channels={Channels}",
            PulseServerLogging.Control, message.Sequence, index, name, volume.Channels);

        if (!_state.TrySetSinkVolume(index, name, volume))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence,
            $"set-sink-volume index={index} name={name} channels={volume.Channels}");
    }

    private async ValueTask HandleSetSourceVolumeAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? index = reader.ReadIndex();
        string? name = reader.ReadString();
        ChannelVolume volume = reader.ReadChannelVolume();
        _logger.LogDebug("{Prefix} seq={Sequence} set-source-volume index={Index} name={Name} channels={Channels}",
            PulseServerLogging.Control, message.Sequence, index, name, volume.Channels);

        if (!_state.TrySetSourceVolume(index, name, volume))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence,
            $"set-source-volume index={index} name={name} channels={volume.Channels}");
    }

    private async ValueTask HandleSetSinkMuteAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? index = reader.ReadIndex();
        string? name = reader.ReadString();
        bool mute = reader.ReadBool();
        _logger.LogDebug("{Prefix} seq={Sequence} set-sink-mute index={Index} name={Name} mute={Mute}",
            PulseServerLogging.Control, message.Sequence, index, name, mute);

        if (!_state.TrySetSinkMute(index, name, mute))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence,
            $"set-sink-mute index={index} name={name} mute={mute}");
    }

    private async ValueTask HandleSetSourceMuteAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? index = reader.ReadIndex();
        string? name = reader.ReadString();
        bool mute = reader.ReadBool();
        _logger.LogDebug("{Prefix} seq={Sequence} set-source-mute index={Index} name={Name} mute={Mute}",
            PulseServerLogging.Control, message.Sequence, index, name, mute);

        if (!_state.TrySetSourceMute(index, name, mute))
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        await session.SendAckAsync(message.Sequence,
            $"set-source-mute index={index} name={name} mute={mute}");
    }

    private async ValueTask HandleSetSinkInputVolumeAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? sinkInputIndex = reader.ReadIndex();
        ChannelVolume volume = reader.ReadChannelVolume();
        _logger.LogDebug("{Prefix} seq={Sequence} set-sink-input-volume index={Index} channels={Channels}",
            PulseServerLogging.Control, message.Sequence, sinkInputIndex, volume.Channels);

        if (sinkInputIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        PlaybackStreamState? stream = _state.GetPlaybackStreamByStreamIndex(sinkInputIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        try
        {
            stream.SetVolume(volume);
        }
        catch (ArgumentOutOfRangeException)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        await session.SendAckAsync(message.Sequence,
            $"set-sink-input-volume index={sinkInputIndex.Value} channels={volume.Channels}");
    }

    private async ValueTask HandleSetSinkInputMuteAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? sinkInputIndex = reader.ReadIndex();
        bool mute = reader.ReadBool();
        _logger.LogDebug("{Prefix} seq={Sequence} set-sink-input-mute index={Index} mute={Mute}",
            PulseServerLogging.Control, message.Sequence, sinkInputIndex, mute);

        if (sinkInputIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        PlaybackStreamState? stream = _state.GetPlaybackStreamByStreamIndex(sinkInputIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        stream.SetMute(mute);
        await session.SendAckAsync(message.Sequence,
            $"set-sink-input-mute index={sinkInputIndex.Value} mute={mute}");
    }

    private async ValueTask HandleSetSourceOutputVolumeAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? sourceOutputIndex = reader.ReadIndex();
        ChannelVolume volume = reader.ReadChannelVolume();
        _logger.LogDebug("{Prefix} seq={Sequence} set-source-output-volume index={Index} channels={Channels}",
            PulseServerLogging.Control, message.Sequence, sourceOutputIndex, volume.Channels);
        await session.SendErrorAsync(message.Sequence,
            sourceOutputIndex == null ? PulseError.Invalid : PulseError.NoEntity);
    }

    private async ValueTask HandleSetSourceOutputMuteAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? sourceOutputIndex = reader.ReadIndex();
        bool mute = reader.ReadBool();
        _logger.LogDebug("{Prefix} seq={Sequence} set-source-output-mute index={Index} mute={Mute}",
            PulseServerLogging.Control, message.Sequence, sourceOutputIndex, mute);
        await session.SendErrorAsync(message.Sequence,
            sourceOutputIndex == null ? PulseError.Invalid : PulseError.NoEntity);
    }

    private async ValueTask HandleUpdateClientProplistAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        PropsUpdateMode mode = ReadPropsUpdateMode(reader);
        Props props = reader.ReadProps();

        session.ClientProperties.Update(mode, props);
        session.ClientName = session.ClientProperties.GetString("application.name")
                             ?? session.ClientProperties.GetString("application.process.binary")
                             ?? session.ClientName;

        await session.SendAckAsync(message.Sequence,
            $"update-client-proplist mode={mode} count={props.Count}");
    }

    private async ValueTask HandleUpdatePlaybackStreamProplistAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? channelIndex = reader.ReadIndex();
        PropsUpdateMode mode = ReadPropsUpdateMode(reader);
        Props props = reader.ReadProps();

        if (channelIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        PlaybackStreamState? stream = _state.GetPlaybackStream(channelIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        stream.UpdateProps(mode, props);
        await session.SendAckAsync(message.Sequence,
            $"update-playback-stream-proplist channel={channelIndex.Value} mode={mode} count={props.Count}");
    }

    private async ValueTask HandleRemoveClientProplistAsync(PulseServerSession session, ProtocolMessage message)
    {
        string[] keys = ReadProplistKeys(message.ReadPayload());
        int removed = session.ClientProperties.RemoveKeys(keys);
        await session.SendAckAsync(message.Sequence,
            $"remove-client-proplist removed={removed} requested={keys.Length}");
    }

    private async ValueTask HandleRemovePlaybackStreamProplistAsync(PulseServerSession session, ProtocolMessage message)
    {
        var reader = message.ReadPayload();
        uint? channelIndex = reader.ReadIndex();
        if (channelIndex == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.Invalid);
            return;
        }

        PlaybackStreamState? stream = _state.GetPlaybackStream(channelIndex.Value);
        if (stream == null)
        {
            await session.SendErrorAsync(message.Sequence, PulseError.NoEntity);
            return;
        }

        string[] keys = ReadProplistKeys(reader);
        int removed = stream.RemoveProps(keys);
        await session.SendAckAsync(message.Sequence,
            $"remove-playback-stream-proplist channel={channelIndex.Value} removed={removed} requested={keys.Length}");
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

    private void LogSinkInfoRequest(ProtocolMessage message)
    {
        if (message.CommandTag == CommandTag.GetSinkInfoList)
        {
            _logger.LogDebug("{Prefix} seq={Sequence} get-sink-info-list",
                PulseServerLogging.Control, message.Sequence);
            return;
        }

        var reader = message.ReadPayload();
        uint? sinkIndex = reader.ReadIndex();
        string? sinkName = reader.ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} get-sink-info index={SinkIndex} name={SinkName}",
            PulseServerLogging.Control, message.Sequence, sinkIndex, sinkName);
    }

    private void LogSourceInfoRequest(ProtocolMessage message)
    {
        if (message.CommandTag == CommandTag.GetSourceInfoList)
        {
            _logger.LogDebug("{Prefix} seq={Sequence} get-source-info-list",
                PulseServerLogging.Control, message.Sequence);
            return;
        }

        var reader = message.ReadPayload();
        uint? sourceIndex = reader.ReadIndex();
        string? sourceName = reader.ReadString();
        _logger.LogDebug("{Prefix} seq={Sequence} get-source-info index={SourceIndex} name={SourceName}",
            PulseServerLogging.Control, message.Sequence, sourceIndex, sourceName);
    }

    private static string SummarizeServerInfo(ServerInfo info)
    {
        return
            $"server-info name={info.ServerName} version={info.ServerVersion} user={info.UserName} host={info.HostName} defaultSinkName={info.DefaultSinkName} defaultSourceName={info.DefaultSourceName} cookie=0x{info.Cookie:x8} sampleSpec={info.SampleSpec.Format}/{info.SampleSpec.Channels}/{info.SampleSpec.SampleRate} channelMapChannels={info.ChannelMap.NumChannels}";
    }

    private static string SummarizeSinkInfo(SinkInfo sink)
    {
        string activePort = sink.Ports.Count > 0 && sink.ActivePortIndex < sink.Ports.Count
            ? sink.Ports[(int)sink.ActivePortIndex].Name
            : "<none>";
        return
            $"sink-info index={sink.Index} name={sink.Name} desc={sink.Description} sampleSpec={sink.SampleSpec.Format}/{sink.SampleSpec.Channels}/{sink.SampleSpec.SampleRate} ownerModuleIndex={sink.OwnerModuleIndex} monitorSourceIndex={sink.MonitorSourceIndex} monitorSourceName={sink.MonitorSourceName} actualLatencyUsec={sink.ActualLatency} configuredLatencyUsec={sink.ConfiguredLatency} mute={sink.Mute} state={sink.State} ports={sink.Ports.Count} activePort={activePort}";
    }

    private static string SummarizeSourceInfo(SourceInfo source)
    {
        string activePort = source.Ports.Count > 0 && source.ActivePortIndex < source.Ports.Count
            ? source.Ports[(int)source.ActivePortIndex].Name
            : "<none>";
        return
            $"source-info index={source.Index} name={source.Name} desc={source.Description} sampleSpec={source.SampleSpec.Format}/{source.SampleSpec.Channels}/{source.SampleSpec.SampleRate} ownerModuleIndex={source.OwnerModuleIndex} monitorSinkIndex={source.MonitorSinkIndex} monitorSinkName={source.MonitorSinkName} actualLatencyUsec={source.ActualLatency} configuredLatencyUsec={source.ConfiguredLatency} mute={source.Mute} state={source.State} ports={source.Ports.Count} activePort={activePort}";
    }

    private static string SummarizeClientInfo(ClientInfo client)
    {
        return
            $"client-info index={client.Index} name={client.Name} ownerModuleIndex={client.OwnerModuleIndex} driver={client.Driver}";
    }

    private static string SummarizeSinkInputInfo(SinkInputInfo sinkInput)
    {
        return
            $"sink-input-info index={sinkInput.Index} name={sinkInput.Name} clientIndex={sinkInput.ClientIndex} sinkIndex={sinkInput.SinkIndex} sampleSpec={sinkInput.SampleSpec.Format}/{sinkInput.SampleSpec.Channels}/{sinkInput.SampleSpec.SampleRate} mute={sinkInput.Mute} corked={sinkInput.Corked} hasVolume={sinkInput.HasVolume}";
    }

    private static string SummarizeStatReply(StatReply stat)
    {
        return
            $"stat memblockTotal={stat.MemblockTotal} memblockUsed={stat.MemblockUsed} memblockPoolUsed={stat.MemblockPoolUsed} bytesReceived={stat.BytesReceived} bytesSent={stat.BytesSent} samplesTotal={stat.SamplesTotal} inputBytesTotal={stat.InputBytesTotal} outputBytesTotal={stat.OutputBytesTotal}";
    }

    private static string SummarizeCreatePlaybackStreamReply(PlaybackStreamState stream, SinkInfo sink)
    {
        return
            $"create-playback-stream-reply channel={stream.ChannelIndex} streamIndex={stream.StreamIndex} requestedBytes={stream.InitialRequestedBytes} sampleSpec={stream.SampleSpec.Format}/{stream.SampleSpec.Channels}/{stream.SampleSpec.SampleRate} sinkIndex={sink.Index} sinkName={sink.Name} bufferAttr=max:{stream.BufferAttr.MaxLength} target:{stream.BufferAttr.TargetLength} pre:{stream.BufferAttr.Prebuffer} minreq:{stream.BufferAttr.MinRequest}";
    }

    private static ClientInfo CreateClientInfo(PulseServerSession session)
    {
        return new ClientInfo
        {
            Index = 1,
            Name = session.ClientName ?? "podish-client",
            OwnerModuleIndex = null,
            Driver = "protocol-native.c",
            Props = session.ClientProperties,
        };
    }

    private static SinkInputInfo CreateSinkInputInfo(PlaybackStreamState stream)
    {
        string? name = stream.StreamName
                       ?? stream.Props.GetString("media.name")
                       ?? stream.ClientName;

        return new SinkInputInfo
        {
            Index = stream.StreamIndex,
            Name = name,
            OwnerModuleIndex = null,
            ClientIndex = 1,
            SinkIndex = 1,
            SampleSpec = stream.SampleSpec,
            ChannelMap = stream.ChannelMap,
            Volume = stream.Volume,
            BufferLatencyUsec = 0,
            SinkLatencyUsec = 0,
            ResampleMethod = "copy",
            Driver = "podish-sdl",
            Mute = stream.Mute,
            Props = stream.Props,
            Corked = stream.Corked,
            HasVolume = true,
            VolumeWritable = true,
        };
    }

    private static PropsUpdateMode ReadPropsUpdateMode(TagStructReader reader)
    {
        uint raw = reader.ReadU32();
        return raw switch
        {
            0 => PropsUpdateMode.Set,
            1 => PropsUpdateMode.Merge,
            2 => PropsUpdateMode.Replace,
            _ => throw new InvalidProtocolMessageException($"Invalid props update mode {raw}"),
        };
    }

    private static string[] ReadProplistKeys(TagStructReader reader)
    {
        var keys = new List<string>();
        while (true)
        {
            string? key = reader.ReadString();
            if (key == null)
                break;
            keys.Add(key);
        }

        return keys.ToArray();
    }
}
