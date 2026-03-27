using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Pulse.Audio;
using Podish.Pulse.Protocol;
using Podish.Pulse.Protocol.Commands;

namespace Podish.Cli.Pulse;

internal sealed class PulseServerSession
{
    private const int DescriptorSize = Constants.DescriptorSize;
    private const uint FlagShmData = 0x80000000;
    private const uint FlagShmDataMemfdBlock = 0x20000000;
    private const int ShmInfoWords = 4;
    private const int ShmInfoSize = ShmInfoWords * sizeof(uint);
    private const int ShmInfoShmIdWord = 1;
    private const int ShmInfoIndexWord = 2;
    private const int ShmInfoLengthWord = 3;

    private readonly VirtualDaemonConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _descriptorBuffer = new byte[DescriptorSize];
    private readonly PulseCommandDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Lock _playbackGate = new();
    private readonly Dictionary<uint, PlaybackStreamState> _playbackStreams = new();
    private readonly Dictionary<uint, LinuxFile> _registeredMemfdByShmId = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly PulseServerState _state;
    private uint _nextChannelIndex;
    private byte[] _payloadBuffer = Array.Empty<byte>();
    private List<LinuxFile>? _pendingAncillaryFds;
    private int _playbackPumpScheduled;
    private byte[] _recvScratch = new byte[64 * 1024];

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
                var descriptorRead = await ReadExactAsync(_descriptorBuffer, _descriptorBuffer.Length);
                if (descriptorRead == 0) break;

                if (descriptorRead != _descriptorBuffer.Length)
                {
                    _logger.LogWarning(
                        "{Prefix} descriptor short read expected={Expected} actual={Actual}",
                        PulseServerLogging.Connection,
                        _descriptorBuffer.Length,
                        descriptorRead);
                    throw new InvalidProtocolMessageException("Unexpected EOF while reading descriptor");
                }

                var descriptor = DescriptorIO.Read(_descriptorBuffer);
                _logger.LogDebug("{Prefix} len={Length} channel={Channel} offset={Offset} flags={Flags}",
                    PulseServerLogging.Descriptor, descriptor.Length, descriptor.Channel, descriptor.Offset,
                    descriptor.Flags);

                EnsurePayloadCapacity((int)descriptor.Length);
                var payloadLength = (int)descriptor.Length;
                if (payloadLength > 0)
                {
                    var payloadRead = await ReadExactAsync(_payloadBuffer, payloadLength);
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
                    var message = ProtocolMessageIO.Decode(descriptor, _payloadBuffer, payloadLength,
                        ClientProtocolVersion);
                    await _dispatcher.DispatchAsync(this, message);
                    DisposePendingAncillaryFds();
                }
                else
                {
                    await HandleStreamPacketAsync(descriptor, _payloadBuffer, payloadLength);
                    DisposePendingAncillaryFds();
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
            CleanupPlaybackStreams();
            DisposePendingAncillaryFds();
            DisposeRegisteredMemfds();
            _logger.LogInformation("{Prefix} client session stopped", PulseServerLogging.Connection);
        }
    }

    public bool TryRegisterMemfdShmid(uint shmId)
    {
        var fds = TakePendingAncillaryFds();
        if (fds is not { Count: 1 })
        {
            DisposeFds(fds);
            return false;
        }

        var memfd = fds[0];
        lock (_playbackGate)
        {
            if (_registeredMemfdByShmId.TryGetValue(shmId, out var previous))
            {
                previous.Close();
                _registeredMemfdByShmId.Remove(shmId);
            }

            _registeredMemfdByShmId[shmId] = memfd;
        }

        return true;
    }

    public bool TryCreatePlaybackStream(CreatePlaybackStreamParams parameters, out PlaybackStreamState? stream,
        out PulseError? error)
    {
        if (!_state.TryAllocatePlaybackStreamIndex(parameters, out var streamIndex, out error))
        {
            stream = null;
            return false;
        }

        uint channelIndex;
        lock (_playbackGate)
        {
            channelIndex = _nextChannelIndex++;
        }

        stream = new PlaybackStreamState(channelIndex, streamIndex, parameters, ClientName);
        _state.RegisterPlaybackStream(stream);
        return true;
    }

    public void AttachPlaybackStream(PlaybackStreamState stream)
    {
        lock (_playbackGate)
        {
            _playbackStreams[stream.ChannelIndex] = stream;
        }

        _state.AudioSink.AttachStream(stream, NotifyPlaybackProgress);
        if (stream.InitialRequestedBytes > 0)
            stream.RecordRequest((int)stream.InitialRequestedBytes);
        RequestPlaybackPump();
    }

    public void DetachPlaybackStream(uint channelIndex)
    {
        PlaybackStreamState? stream;
        lock (_playbackGate)
        {
            _playbackStreams.TryGetValue(channelIndex, out stream);
            _playbackStreams.Remove(channelIndex);
        }

        if (stream != null)
            _state.RemovePlaybackStream(stream.StreamIndex);

        _state.AudioSink.DetachStream(channelIndex);
    }

    public PlaybackStreamState? GetPlaybackStreamByChannelIndex(uint channelIndex)
    {
        lock (_playbackGate)
        {
            _playbackStreams.TryGetValue(channelIndex, out var stream);
            return stream;
        }
    }

    public StatReply CreateStatReply()
    {
        var snapshot = GetPlaybackStreamsSnapshot();
        var buffered = 0;
        var outputBuffered = 0;
        foreach (var stream in snapshot)
        {
            buffered += stream.BufferedBytes;
            outputBuffered += stream.QueuedOutputEstimateBytes;
        }

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
            SamplesTotal = (uint)snapshot.Sum(static stream => stream.SampleSpec.SampleRate),
            InputBytesTotal = (ulong)buffered,
            OutputBytesTotal = (ulong)outputBuffered
        };
    }

    public ValueTask SendAckAsync(uint sequence, string? summary = null)
    {
        if (!string.IsNullOrEmpty(summary))
            _logger.LogDebug("{Prefix} reply seq={Sequence} ack {Summary}",
                PulseServerLogging.Control, sequence, summary);

        return SendRawAsync(ProtocolMessageIO.EncodeAck(sequence, ClientProtocolVersion));
    }

    public ValueTask SendErrorAsync(uint sequence, PulseError error, string? summary = null)
    {
        _logger.LogDebug("{Prefix} reply seq={Sequence} error={Error}{SummarySuffix}",
            PulseServerLogging.Control, sequence, error,
            string.IsNullOrEmpty(summary) ? string.Empty : $" {summary}");
        return SendRawAsync(ProtocolMessageIO.EncodeError(sequence, error, ClientProtocolVersion));
    }

    public ValueTask SendReplyAsync<T>(uint sequence, T reply, Action<TagStructWriter, T> writerAction,
        string? summary = null)
    {
        if (!string.IsNullOrEmpty(summary))
            _logger.LogDebug("{Prefix} reply seq={Sequence} {Summary}",
                PulseServerLogging.Control, sequence, summary);

        return SendRawAsync(ProtocolMessageIO.EncodeReply(sequence, reply, writerAction, ClientProtocolVersion));
    }

    public async ValueTask MaybeSendPlaybackRequestAsync(PlaybackStreamState stream)
    {
        if (!IsAttachedPlaybackStream(stream.ChannelIndex))
            return;

        if (!stream.TryBeginRequest())
            return;

        try
        {
            if (!stream.ShouldRequestMore())
                return;

            if (stream.PendingRequestedBytes >= stream.RequestBytesHint)
            {
                _logger.LogTrace(
                    "{Prefix} suppress request stream={ChannelIndex} outputEstimate={OutputEstimate} buffered={Buffered} pending={Pending} hint={Hint}",
                    PulseServerLogging.Stream,
                    stream.ChannelIndex,
                    stream.QueuedOutputEstimateBytes,
                    stream.BufferedBytes,
                    stream.PendingRequestedBytes,
                    stream.RequestBytesHint);
                return;
            }

            var bytesNeeded = stream.TargetBytesHint -
                              (stream.QueuedOutputEstimateBytes + stream.PendingRequestedBytes);
            var requestBytes = Math.Max(stream.RequestBytesHint, bytesNeeded);
            stream.RecordRequest(requestBytes);
            _logger.LogDebug(
                "{Prefix} request stream={ChannelIndex} requestBytes={RequestBytes} buffered={Buffered} outputEstimate={OutputEstimate} pending={Pending}",
                PulseServerLogging.Stream, stream.ChannelIndex, requestBytes, stream.BufferedBytes,
                stream.QueuedOutputEstimateBytes, stream.PendingRequestedBytes);
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
            stream.EndRequest();
        }
    }

    public async ValueTask TryCompleteDrainAsync(PlaybackStreamState stream)
    {
        if (stream.PendingDrainSequence == null)
            return;

        if (stream.HasPendingOutput)
            return;

        var seq = stream.PendingDrainSequence.Value;
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
        var stream = GetPlaybackStreamByChannelIndex(descriptor.Channel);
        if (stream == null)
        {
            _logger.LogWarning("{Prefix} dropping packet for unknown channel={Channel}", PulseServerLogging.Stream,
                descriptor.Channel);
            return;
        }

        ReadOnlySpan<byte> data = payload.AsSpan(0, payloadLength);
        var dataLength = payloadLength;
        var flags = (uint)descriptor.Flags;
        if ((flags & FlagShmData) != 0)
        {
            if ((flags & FlagShmDataMemfdBlock) == 0)
            {
                _logger.LogWarning("{Prefix} dropping posix-shm packet for channel={Channel}",
                    PulseServerLogging.Stream, descriptor.Channel);
                return;
            }

            if (payloadLength < ShmInfoSize)
            {
                _logger.LogWarning("{Prefix} dropping short memfd packet for channel={Channel} payload={PayloadLength}",
                    PulseServerLogging.Stream, descriptor.Channel, payloadLength);
                return;
            }

            var shmId = ReadUInt32NetworkOrder(payload.AsSpan(ShmInfoShmIdWord * sizeof(uint), sizeof(uint)));
            var memfdIndex = checked((int)ReadUInt32NetworkOrder(
                payload.AsSpan(ShmInfoIndexWord * sizeof(uint), sizeof(uint))));
            dataLength = checked((int)ReadUInt32NetworkOrder(
                payload.AsSpan(ShmInfoLengthWord * sizeof(uint), sizeof(uint))));

            if (!TryAppendMemfdPayload(stream, shmId, memfdIndex, dataLength, out var buffered))
            {
                _logger.LogWarning("{Prefix} dropping unresolved memfd packet for channel={Channel} shmId={ShmId}",
                    PulseServerLogging.Stream, descriptor.Channel, shmId);
                return;
            }

            _logger.LogDebug("{Prefix} stream={ChannelIndex} appended={Bytes} buffered={Buffered}",
                PulseServerLogging.Stream, descriptor.Channel, dataLength, buffered);
        }
        else
        {
            var buffered = stream.Append(data[..dataLength]);
            _logger.LogDebug("{Prefix} stream={ChannelIndex} appended={Bytes} buffered={Buffered}",
                PulseServerLogging.Stream, descriptor.Channel, dataLength, buffered);
        }

        if (stream.TryMarkStarted())
        {
            var started = ProtocolMessage.Create(CommandTag.Started, uint.MaxValue,
                writer => writer.WriteU32(stream.ChannelIndex), ClientProtocolVersion);
            await SendRawAsync(ProtocolMessageIO.Encode(started, ClientProtocolVersion));
        }

        NotifyPlaybackStateChanged();
        await TryCompleteDrainAsync(stream);
        await MaybeSendPlaybackRequestAsync(stream);
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int bytesNeeded)
    {
        var total = 0;
        while (total < bytesNeeded)
        {
            var requested = bytesNeeded - total;
            EnsureScratchCapacity(requested);
            var message = await _connection.RecvMsgAsync(_recvScratch, 0, requested);
            var read = message.BytesRead;
            AppendPendingAncillaryFds(message.Fds);

            if (read > 0) Buffer.BlockCopy(_recvScratch, 0, buffer, total, read);

            if (read == -(int)Errno.EINTR)
                continue;

            if (read == -(int)Errno.EAGAIN)
                throw new IOException("Unexpected EAGAIN while reading from blocking PulseAudio session socket");

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

    private bool TryAppendMemfdPayload(PlaybackStreamState stream, uint shmId, int offset, int length, out int buffered)
    {
        buffered = stream.BufferedBytes;
        if (offset < 0 || length < 0)
            return false;

        LinuxFile? memfd;
        lock (_playbackGate)
        {
            _registeredMemfdByShmId.TryGetValue(shmId, out memfd);
        }

        if (memfd?.OpenedInode == null)
            return false;

        var written = 0;
        var currentBuffered = buffered;
        var segments = memfd.OpenedInode.GetReadableSegments(memfd, offset, length);
        foreach (var chunk in segments)
        {
            currentBuffered = stream.Append(chunk);
            written += chunk.Length;
        }

        if (!segments.Succeeded || written != length)
            return false;

        buffered = currentBuffered;
        return true;
    }

    private async ValueTask PumpPlaybackAsync()
    {
        foreach (var stream in GetPlaybackStreamsSnapshot())
        {
            await TryCompleteDrainAsync(stream);
            await MaybeSendPlaybackRequestAsync(stream);
        }
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

        if (!CanSchedulePlaybackPump())
            return;

        if (Interlocked.Exchange(ref _playbackPumpScheduled, 1) == 0)
            _connection.Runtime.Scheduler.Schedule(() => { _ = RunPlaybackPumpTickAsync(); }, _connection.Task);
    }

    private async Task RunPlaybackPumpTickAsync()
    {
        try
        {
            if (_cts.IsCancellationRequested || !CanSchedulePlaybackPump())
                return;

            await PumpPlaybackAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _playbackPumpScheduled, 0);
        }
    }

    private void CleanupPlaybackStreams()
    {
        PlaybackStreamState[] streams;
        lock (_playbackGate)
        {
            streams = _playbackStreams.Values.ToArray();
            _playbackStreams.Clear();
        }

        foreach (var stream in streams)
        {
            stream.Clear();
            _state.RemovePlaybackStream(stream.StreamIndex);
            _state.AudioSink.DetachStream(stream.ChannelIndex);
        }

        if (streams.Length > 0)
            _state.AudioSink.NotifyStreamStateChanged();
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
        var offset = 0;
        while (offset < payload.Length)
        {
            var sent = await _connection.SendAsync(payload.AsMemory(offset, payload.Length - offset));
            if (sent <= 0)
                throw new IOException("Socket closed while sending PulseAudio packet");
            offset += sent;
        }
    }

    private void LogOutgoingPacket(byte[] payload)
    {
        if (payload.Length < DescriptorSize)
            return;

        var descriptor = DescriptorIO.Read(payload);
        _logger.LogDebug("{Prefix} outgoing len={Length} channel={Channel} offset={Offset} flags={Flags}",
            PulseServerLogging.Descriptor, descriptor.Length, descriptor.Channel, descriptor.Offset, descriptor.Flags);

        var payloadLength = Math.Max(0, payload.Length - DescriptorSize);
        if (descriptor.Channel != uint.MaxValue)
            return;

        try
        {
            var message = ProtocolMessageIO.Decode(payload, ClientProtocolVersion);
            _logger.LogDebug("{Prefix} outgoing seq={Sequence} cmd={Command} payloadLen={PayloadLen}",
                PulseServerLogging.Control, message.Sequence, message.CommandTag, message.Payload.Length);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Prefix} failed to decode outgoing control packet",
                PulseServerLogging.Control);
        }
    }

    private void AppendPendingAncillaryFds(List<LinuxFile>? fds)
    {
        if (fds == null || fds.Count == 0)
            return;

        _pendingAncillaryFds ??= new List<LinuxFile>(fds.Count);
        _pendingAncillaryFds.AddRange(fds);
    }

    private List<LinuxFile>? TakePendingAncillaryFds()
    {
        var fds = _pendingAncillaryFds;
        _pendingAncillaryFds = null;
        return fds;
    }

    private void DisposePendingAncillaryFds()
    {
        DisposeFds(_pendingAncillaryFds);
        _pendingAncillaryFds = null;
    }

    private void DisposeRegisteredMemfds()
    {
        lock (_playbackGate)
        {
            foreach (var memfd in _registeredMemfdByShmId.Values)
                memfd.Close();
            _registeredMemfdByShmId.Clear();
        }
    }

    private static void DisposeFds(List<LinuxFile>? fds)
    {
        if (fds == null)
            return;

        foreach (var fd in fds)
            fd.Close();
    }

    private static uint ReadUInt32NetworkOrder(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private void EnsurePayloadCapacity(int requiredBytes)
    {
        if (_payloadBuffer.Length >= requiredBytes)
            return;

        var newSize = Math.Max(requiredBytes, Math.Max(4096, _payloadBuffer.Length * 2));
        _payloadBuffer = new byte[newSize];
    }

    private void EnsureScratchCapacity(int requiredBytes)
    {
        if (_recvScratch.Length >= requiredBytes)
            return;

        var newSize = Math.Max(requiredBytes, _recvScratch.Length * 2);
        _recvScratch = new byte[newSize];
    }

    private PlaybackStreamState[] GetPlaybackStreamsSnapshot()
    {
        lock (_playbackGate)
        {
            return _playbackStreams.Values.ToArray();
        }
    }

    private bool IsAttachedPlaybackStream(uint channelIndex)
    {
        lock (_playbackGate)
        {
            return _playbackStreams.ContainsKey(channelIndex);
        }
    }

    private bool CanSchedulePlaybackPump()
    {
        var task = _connection.Task;
        return !task.IsRetiring &&
               !task.Exited &&
               task.Status != FiberTaskStatus.Terminated &&
               task.Status != FiberTaskStatus.Zombie;
    }
}