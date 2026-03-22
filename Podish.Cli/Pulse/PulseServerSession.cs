using Microsoft.Extensions.Logging;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;
using Fiberish.Core;
using Fiberish.Native;
using System.Threading;
using Fiberish.VFS;

namespace Podish.Cli.Pulse;

internal sealed class PulseServerSession
{
    private const int DescriptorSize = Constants.DescriptorSize;

    private readonly VirtualDaemonConnection _connection;
    private readonly PulseCommandDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly PulseServerState _state;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _descriptorBuffer = new byte[DescriptorSize];
    private byte[] _payloadBuffer = Array.Empty<byte>();
    private byte[] _recvScratch = new byte[64 * 1024];
    private PlaybackStreamState? _playbackStream;
    private int _requestInFlight;
    private int _playbackPumpScheduled;

    public PulseServerSession(VirtualDaemonConnection connection, PulseServerState state, ILogger logger)
    {
        _connection = connection;
        _state = state;
        _logger = logger;
        _dispatcher = new PulseCommandDispatcher(state, logger);
        ClientProtocolVersion = Constants.MaxVersion;
        ClientProperties = new Props();
    }

    public ushort ClientProtocolVersion { get; set; }
    public string? ClientName { get; set; }
    public Props ClientProperties { get; set; }
    public ulong BytesReceived { get; private set; }
    public ulong BytesSent { get; private set; }

    public async Task RunAsync()
    {
        _connection.File.Flags &= ~FileFlags.O_NONBLOCK;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int descriptorRead = await ReadExactAsync(_descriptorBuffer, _descriptorBuffer.Length);
                if (descriptorRead == 0)
                {
                    break;
                }

                if (descriptorRead != _descriptorBuffer.Length)
                {
                    _logger.LogWarning(
                        "{Prefix} descriptor short read expected={Expected} actual={Actual}",
                        PulseServerLogging.Connection,
                        _descriptorBuffer.Length,
                        descriptorRead);
                    throw new InvalidProtocolMessageException("Unexpected EOF while reading descriptor");
                }

                Descriptor descriptor = DescriptorIO.Read(_descriptorBuffer);
                _logger.LogDebug("{Prefix} len={Length} channel={Channel} offset={Offset} flags={Flags}",
                    PulseServerLogging.Descriptor, descriptor.Length, descriptor.Channel, descriptor.Offset,
                    descriptor.Flags);

                EnsurePayloadCapacity((int)descriptor.Length);
                int payloadLength = (int)descriptor.Length;
                if (payloadLength > 0)
                {
                    int payloadRead = await ReadExactAsync(_payloadBuffer, payloadLength);
                    if (payloadRead != payloadLength)
                    {
                        _logger.LogWarning(
                            "{Prefix} payload short read expected={Expected} actual={Actual} channel={Channel}",
                            PulseServerLogging.Connection,
                            payloadLength,
                            payloadRead,
                            descriptor.Channel);
                        throw new InvalidProtocolMessageException("Unexpected EOF while reading payload");
                    }
                }

                BytesReceived += (ulong)(DescriptorSize + payloadLength);
                if (descriptor.Channel == uint.MaxValue)
                {
                    ProtocolMessage message = ProtocolMessageIO.Decode(descriptor, _payloadBuffer, payloadLength,
                        ClientProtocolVersion);
                    await _dispatcher.DispatchAsync(this, message);
                }
                else
                {
                    await HandleStreamPacketAsync(descriptor, _payloadBuffer, payloadLength);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} client session failed", PulseServerLogging.Connection);
            throw;
        }
        finally
        {
            _cts.Cancel();
            _logger.LogInformation("{Prefix} client session stopped", PulseServerLogging.Connection);
        }
    }

    public void AttachPlaybackStream(PlaybackStreamState stream)
    {
        _playbackStream = stream;
        _requestInFlight = 0;
        _state.AudioSink.AttachStream(stream, NotifyPlaybackProgress);
        if (stream.InitialRequestedBytes > 0)
            stream.RecordRequest((int)stream.InitialRequestedBytes);
        RequestPlaybackPump();
    }

    public void DetachPlaybackStream(uint channelIndex)
    {
        if (_playbackStream?.ChannelIndex == channelIndex)
        {
            _state.AudioSink.DetachStream(channelIndex);
            _playbackStream = null;
            _requestInFlight = 0;
        }
    }

    public StatReply CreateStatReply()
    {
        int buffered = _playbackStream?.BufferedBytes ?? 0;
        return new StatReply
        {
            MemblockTotal = (ulong)buffered,
            MemblockUsed = (ulong)buffered,
            MemblockTotalServer = (ulong)buffered,
            MemblockUsedServer = (ulong)buffered,
            BytesReceived = BytesReceived,
            BytesSent = BytesSent,
            MemblockPoolSize = 1,
            MemblockPoolUsed = (uint)buffered,
            MemblockPoolAllocated = (uint)buffered,
            SamplesTotal = (uint)(_playbackStream?.SampleSpec.SampleRate ?? 0),
            InputBytesTotal = (ulong)buffered,
            OutputBytesTotal = (ulong)_state.AudioSink.QueuedBytes,
        };
    }

    public ValueTask SendAckAsync(uint sequence)
    {
        return SendRawAsync(ProtocolMessageIO.EncodeAck(sequence));
    }

    public ValueTask SendErrorAsync(uint sequence, PulseError error)
    {
        return SendRawAsync(ProtocolMessageIO.EncodeError(sequence, error));
    }

    public ValueTask SendReplyAsync<T>(uint sequence, T reply, Action<TagStructWriter, T> writerAction)
    {
        return SendRawAsync(ProtocolMessageIO.EncodeReply(sequence, reply, writerAction));
    }

    public async ValueTask MaybeSendPlaybackRequestAsync(PlaybackStreamState stream)
    {
        if (_playbackStream?.ChannelIndex != stream.ChannelIndex)
            return;

        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0)
            return;

        try
        {
            if (!stream.ShouldRequestMore(_state.AudioSink.QueuedBytes))
                return;

            if (stream.PendingRequestedBytes >= stream.RequestBytesHint)
            {
                _logger.LogTrace(
                    "{Prefix} suppress request stream={ChannelIndex} queued={Queued} buffered={Buffered} pending={Pending} hint={Hint}",
                    PulseServerLogging.Stream,
                    stream.ChannelIndex,
                    _state.AudioSink.QueuedBytes,
                    stream.BufferedBytes,
                    stream.PendingRequestedBytes,
                    stream.RequestBytesHint);
                return;
            }

            int bytesNeeded = stream.TargetBytesHint - (stream.BufferedBytes + _state.AudioSink.QueuedBytes + stream.PendingRequestedBytes);
            int requestBytes = Math.Max(stream.RequestBytesHint, bytesNeeded);
            stream.RecordRequest(requestBytes);
            _logger.LogDebug("{Prefix} request stream={ChannelIndex} requestBytes={RequestBytes} buffered={Buffered} queued={Queued} pending={Pending}",
                PulseServerLogging.Stream, stream.ChannelIndex, requestBytes, stream.BufferedBytes,
                _state.AudioSink.QueuedBytes, stream.PendingRequestedBytes);
            var request = ProtocolMessage.Create(CommandTag.Request, uint.MaxValue, writer =>
                {
                    writer.WriteU32(stream.ChannelIndex);
                    writer.WriteU32((uint)requestBytes);
                },
                ClientProtocolVersion);
            await SendRawAsync(ProtocolMessageIO.Encode(request, ClientProtocolVersion));
        }
        finally
        {
            Interlocked.Exchange(ref _requestInFlight, 0);
        }
    }

    public async ValueTask TryCompleteDrainAsync(PlaybackStreamState stream)
    {
        if (stream.PendingDrainSequence == null)
            return;

        if (stream.BufferedBytes > 0 || _state.AudioSink.QueuedBytes > 0)
            return;

        uint seq = stream.PendingDrainSequence.Value;
        stream.ClearPendingDrain();
        await SendAckAsync(seq);
    }

    public void NotifyPlaybackStateChanged()
    {
        _state.AudioSink.NotifyStreamStateChanged();
        RequestPlaybackPump();
    }

    private async Task HandleStreamPacketAsync(Descriptor descriptor, byte[] payload, int payloadLength)
    {
        if (_playbackStream == null || _playbackStream.ChannelIndex != descriptor.Channel)
        {
            _logger.LogWarning("{Prefix} dropping packet for unknown channel={Channel}", PulseServerLogging.Stream,
                descriptor.Channel);
            return;
        }

        int buffered = _playbackStream.Append(payload.AsSpan(0, payloadLength));
        _logger.LogDebug("{Prefix} stream={ChannelIndex} appended={Bytes} buffered={Buffered}",
            PulseServerLogging.Stream, descriptor.Channel, payloadLength, buffered);

        if (_playbackStream.TryMarkStarted())
        {
            var started = ProtocolMessage.Create(CommandTag.Started, uint.MaxValue,
                writer => writer.WriteU32(_playbackStream.ChannelIndex), ClientProtocolVersion);
            await SendRawAsync(ProtocolMessageIO.Encode(started, ClientProtocolVersion));
        }

        NotifyPlaybackStateChanged();
        await TryCompleteDrainAsync(_playbackStream);
        await MaybeSendPlaybackRequestAsync(_playbackStream);
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int bytesNeeded)
    {
        int total = 0;
        while (total < bytesNeeded)
        {
            int requested = bytesNeeded - total;
            int read;
            if (total == 0)
            {
                read = await _connection.RecvAsync(buffer, 0, requested);
            }
            else
            {
                EnsureScratchCapacity(requested);
                read = await _connection.RecvAsync(_recvScratch, 0, requested);
            }

            if (read == -(int)Errno.EINTR)
                continue;

            if (read == -(int)Errno.EAGAIN)
            {
                throw new IOException("Unexpected EAGAIN while reading from blocking PulseAudio session socket");
            }

            if (read > 0 && total > 0)
            {
                Buffer.BlockCopy(_recvScratch, 0, buffer, total, read);
            }

            if (read > 0)
            {
                total += read;
                continue;
            }

            if (read <= 0)
            {
                _logger.LogTrace("{Prefix} recv terminating read={Read} total={Total} target={Target}",
                    PulseServerLogging.Connection, read, total, bytesNeeded);
                return total;
            }

        }

        return total;
    }

    private async ValueTask PumpPlaybackAsync()
    {
        var stream = _playbackStream;
        if (stream == null)
            return;

        await TryCompleteDrainAsync(stream);
        await MaybeSendPlaybackRequestAsync(stream);
    }

    private void NotifyPlaybackProgress()
    {
        if (_cts.IsCancellationRequested)
            return;
        RequestPlaybackPump();
    }

    private void RequestPlaybackPump()
    {
        if (_cts.IsCancellationRequested)
            return;

        if (Interlocked.Exchange(ref _playbackPumpScheduled, 1) == 0)
        {
            _connection.Runtime.Scheduler.Schedule(() =>
            {
                _ = RunPlaybackPumpTickAsync();
            }, _connection.Task);
        }
    }

    private async Task RunPlaybackPumpTickAsync()
    {
        try
        {
            var stream = _playbackStream;
            if (stream == null || _cts.IsCancellationRequested)
                return;

            await PumpPlaybackAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _playbackPumpScheduled, 0);
        }
    }

    private async ValueTask SendRawAsync(byte[] payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            BytesSent += (ulong)payload.Length;
            LogOutgoingPacket(payload);
            await SendRawLockedAsync(payload);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async ValueTask SendRawLockedAsync(byte[] payload)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            int sent = await _connection.SendAsync(payload.AsMemory(offset, payload.Length - offset), 0);
            if (sent <= 0)
                throw new IOException("Socket closed while sending PulseAudio packet");
            offset += sent;
        }
    }

    private void LogOutgoingPacket(byte[] payload)
    {
        if (payload.Length < DescriptorSize)
            return;

        Descriptor descriptor = DescriptorIO.Read(payload);
        _logger.LogDebug("{Prefix} outgoing len={Length} channel={Channel} offset={Offset} flags={Flags}",
            PulseServerLogging.Descriptor, descriptor.Length, descriptor.Channel, descriptor.Offset, descriptor.Flags);

        int payloadLength = Math.Max(0, payload.Length - DescriptorSize);
        if (descriptor.Channel != uint.MaxValue)
            return;

        try
        {
            ProtocolMessage message = ProtocolMessageIO.Decode(payload, ClientProtocolVersion);
            _logger.LogDebug("{Prefix} outgoing seq={Sequence} cmd={Command} payloadLen={PayloadLen}",
                PulseServerLogging.Control, message.Sequence, message.CommandTag, message.Payload.Length);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Prefix} failed to decode outgoing control packet",
                PulseServerLogging.Control);
        }
    }

    private void EnsurePayloadCapacity(int requiredBytes)
    {
        if (_payloadBuffer.Length >= requiredBytes)
            return;

        int newSize = Math.Max(requiredBytes, Math.Max(4096, _payloadBuffer.Length * 2));
        _payloadBuffer = new byte[newSize];
    }

    private void EnsureScratchCapacity(int requiredBytes)
    {
        if (_recvScratch.Length >= requiredBytes)
            return;

        int newSize = Math.Max(requiredBytes, _recvScratch.Length * 2);
        _recvScratch = new byte[newSize];
    }
}
