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
        return session.SendReplyAsync(message.Sequence, new AuthReply
        {
            Version = session.ClientProtocolVersion,
            UseShm = false,
            UseMemfd = false,
        }, static (writer, reply) => writer.WriteAuthReply(reply),
            $"auth version={session.ClientProtocolVersion} useShm={false} useMemfd={false}");
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
            Mute = false,
            Props = stream.Props,
            Corked = stream.Corked,
            HasVolume = true,
            VolumeWritable = true,
        };
    }
}
