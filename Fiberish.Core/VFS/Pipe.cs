using System.Buffers;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

// For Errno


namespace Fiberish.VFS;

public class PipeInode : Inode, ITaskWaitSource, IDispatcherWaitSource, IDispatcherEdgeWaitSource
{
    private const int BufferSize = 65536; // 64KB pipe buffer
    private const int MinPipeCapacity = 4096;
    private const int MaxPipeCapacity = 1024 * 1024;
    public const int PipeBuf = 4096;
    private byte[] _buffer;
    private int _capacity = BufferSize;
    private readonly bool _packetMode;
    private readonly Queue<int> _packetLengths = new();

    // Notification handles
    private readonly WaitHandle _readHandle;
    private readonly PooledReadWriteWaitQueues _waitQueues;
    private readonly WaitHandle _writeHandle;
    private int _count;
    private int _head; // Write position
    private bool _lifecycleClosed;
    private int _readerCount;
    private bool _readersClosed;
    private int _tail; // Read position
    private int _writerCount;
    private bool _writersClosed;

    public PipeInode(KernelScheduler scheduler, bool packetMode = false)
    {
        _packetMode = packetMode;
        _waitQueues = new PooledReadWriteWaitQueues(scheduler);
        _readHandle = _waitQueues.ReadQueue;
        _writeHandle = _waitQueues.WriteQueue;
        Type = InodeType.Fifo;
        Mode = 0x1000 | 0x1FF; // FIFO + 777
        AnonymousInodeLifecycle.Initialize(this, "PipeInode.ctor");
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        // Initially writable (empty)
        _writeHandle.Set();
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Pipe readiness wait requires an explicit scheduler.");
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return false;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            if ((events & POLLIN) != 0)
                registered |= QueueReadinessRegistration.Register(callback, scheduler, events, readWatch);

            if ((events & POLLOUT) != 0)
                registered |= QueueReadinessRegistration.Register(callback, scheduler, events, writeWatch);
        }

        return registered;
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Pipe readiness wait requires an explicit scheduler.");
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch, writeWatch);
        }
    }

    IDisposable? IDispatcherEdgeWaitSource.RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile,
        IReadyDispatcher dispatcher, Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            return QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, scheduler, events, readWatch,
                writeWatch);
        }
    }

    public bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return false;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            if ((events & POLLIN) != 0)
                registered |= QueueReadinessRegistration.Register(callback, task, events, readWatch);

            if ((events & POLLOUT) != 0)
                registered |= QueueReadinessRegistration.Register(callback, task, events, writeWatch);
        }

        return registered;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            return QueueReadinessRegistration.RegisterHandle(callback, task, events, readWatch, writeWatch);
        }
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        return default;
    }

    public void AddReader()
    {
        using (EnterStateScope())
        {
            _readerCount++;
            _readersClosed = false;
        }
    }

    public void AddWriter()
    {
        using (EnterStateScope())
        {
            _writerCount++;
            _writersClosed = false;
            // If writers were closed, reopening might affect read handle? No.
        }
    }

    public void RemoveReader()
    {
        using (EnterStateScope())
        {
            _readerCount--;
            if (_readerCount <= 0)
            {
                _readersClosed = true;
                _writeHandle.Set(); // Wake up writers (EPIPE)
            }

            if (_readerCount <= 0 && _writerCount <= 0)
                CloseLifecycleOnce();
        }
    }

    public void RemoveWriter()
    {
        using (EnterStateScope())
        {
            _writerCount--;
            if (_writerCount <= 0)
            {
                _writersClosed = true;
                _readHandle.Set(); // Wake up readers (EOF)
            }

            if (_readerCount <= 0 && _writerCount <= 0)
                CloseLifecycleOnce();
        }
    }

    public override void Open(LinuxFile linuxFile)
    {
        const int O_ACCMODE = 3;
        var mode = (int)linuxFile.Flags & O_ACCMODE;
        if (mode == (int)FileFlags.O_RDONLY)
        {
            AddReader();
        }
        else if (mode == (int)FileFlags.O_RDWR)
        {
            AddReader();
            AddWriter();
        }
        else
        {
            AddWriter();
        }
    }

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer,
        long offset)
    {
        _ = task;
        using (EnterStateScope())
        {
            if (buffer.Length == 0) return 0;
            if (_lifecycleClosed)
                return 0;

            if (_count > 0)
            {
                // Read data
                var consumed = _packetMode ? _packetLengths.Peek() : Math.Min(buffer.Length, _count);
                var available = Math.Min(buffer.Length, consumed);
                var firstChunk = Math.Min(available, BufferSize - _tail);

                if (firstChunk > 0)
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer[..firstChunk]);

                if (available > firstChunk)
                {
                    var secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, 0, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                }

                // A short packet read discards the unread tail of that packet.
                _tail = (_tail + consumed) % BufferSize;
                _count -= consumed;
                if (_packetMode) _packetLengths.Dequeue();

                // Update handles
                if (_count == 0) _readHandle.Reset();
                _writeHandle.Set(); // Space available

                return available;
            }

            if (_writersClosed) return 0; // EOF

            return -(int)Errno.EAGAIN;
        }
    }

    public int Peek(Span<byte> buffer)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return 0;

            if (_count > 0)
            {
                var available = Math.Min(buffer.Length, _packetMode ? _packetLengths.Peek() : _count);
                var firstChunk = Math.Min(available, BufferSize - _tail);

                if (firstChunk > 0)
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer[..firstChunk]);

                if (available > firstChunk)
                {
                    var secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, 0, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                }

                return available;
            }

            if (_writersClosed) return 0; // EOF

            return -(int)Errno.EAGAIN;
        }
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile linuxFile,
        ReadOnlySpan<byte> buffer, long offset)
    {
        _ = task;
        using (EnterStateScope())
        {
            if (buffer.Length == 0) return 0;
            if (_lifecycleClosed)
                return -(int)Errno.EPIPE;

            if (_readersClosed)
                // Broken pipe
                // Send SIGPIPE to current task?
                return -(int)Errno.EPIPE;

            var space = _capacity - _count;
            if (space > 0)
            {
                // Linux pipe(7): blocking writes of n <= PIPE_BUF bytes must remain atomic,
                // so we only report progress here when the pipe can accept the whole chunk.
                // Non-blocking writes of n <= PIPE_BUF bytes return EAGAIN instead of partial progress.
                var requested = _packetMode ? Math.Min(buffer.Length, PipeBuf) : buffer.Length;
                if (requested <= PipeBuf && space < requested)
                    return -(int)Errno.EAGAIN;

                var toWrite = Math.Min(requested, space);
                var firstChunk = Math.Min(toWrite, BufferSize - _head);

                if (firstChunk > 0)
                {
                    buffer[..firstChunk].CopyTo(new Span<byte>(_buffer, _head, firstChunk));
                    _head = (_head + firstChunk) % BufferSize;
                }

                if (toWrite > firstChunk)
                {
                    var secondChunk = toWrite - firstChunk;
                    buffer.Slice(firstChunk, secondChunk).CopyTo(new Span<byte>(_buffer, _head, secondChunk));
                    _head = (_head + secondChunk) % BufferSize;
                }

                _count += toWrite;
                if (_packetMode) _packetLengths.Enqueue(toWrite);

                // Update handles
                if (_count >= _capacity) _writeHandle.Reset();
                _readHandle.Set(); // Data available

                return toWrite;
            }

            return -(int)Errno.EAGAIN;
        }
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        if (_packetMode) return ReadPacketV(engine, file, task, iovs);
        return ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        if (_packetMode) return WritePacketV(engine, file, task, iovs);
        return WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    private async ValueTask<int> ReadPacketV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs)
    {
        var capacity = Math.Min(PipeBuf, iovs.Sum(iov => (long)iov.Len));
        if (capacity == 0) return 0;
        var buffer = ArrayPool<byte>.Shared.Rent((int)capacity);
        try
        {
            while (true)
            {
                var count = Peek(buffer.AsSpan(0, (int)capacity));
                if (count == -(int)Errno.EAGAIN)
                {
                    if ((file.Flags & FileFlags.O_NONBLOCK) != 0 || task == null) return count;
                    if (await WaitForRead(file, task) == AwaitResult.Interrupted) return -(int)Errno.ERESTARTSYS;
                    continue;
                }
                if (count <= 0) return count;
                var copied = 0;
                foreach (var iov in iovs)
                {
                    var length = (int)Math.Min(iov.Len, (uint)(count - copied));
                    if (length != 0 && !engine.CopyToUser(iov.BaseAddr, buffer.AsSpan(copied, length)))
                        return -(int)Errno.EFAULT;
                    copied += length;
                    if (copied == count) break;
                }
                // Consume once, after the scatter copy: guest page/iovec
                // boundaries must not accidentally split or discard a packet.
                return ReadToHost(task, file, buffer.AsSpan(0, count), 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<int> WritePacketV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs)
    {
        var remaining = iovs.Sum(iov => (long)iov.Len);
        if (remaining == 0) return 0;
        var buffer = ArrayPool<byte>.Shared.Rent(PipeBuf);
        var total = 0;
        var index = 0;
        uint offset = 0;
        try
        {
            while (remaining > 0)
            {
                var packetSize = (int)Math.Min(PipeBuf, remaining);
                var copied = 0;
                while (copied < packetSize)
                {
                    var iov = iovs[index];
                    var length = (int)Math.Min(iov.Len - offset, (uint)(packetSize - copied));
                    if (length != 0 && !engine.CopyFromUser(iov.BaseAddr + offset, buffer.AsSpan(copied, length)))
                        return total > 0 ? total : -(int)Errno.EFAULT;
                    copied += length;
                    offset += (uint)length;
                    if (offset == iov.Len) { index++; offset = 0; }
                }
                while (true)
                {
                    var rc = WriteFromHost(task, file, buffer.AsSpan(0, packetSize), 0);
                    if (rc == -(int)Errno.EAGAIN && (file.Flags & FileFlags.O_NONBLOCK) == 0 && task != null)
                    {
                        if (await WaitForWrite(file, task, packetSize) == AwaitResult.Interrupted)
                            return total > 0 ? total : -(int)Errno.ERESTARTSYS;
                        continue;
                    }
                    if (rc == -(int)Errno.EPIPE) task?.PostSignal((int)Signal.SIGPIPE);
                    if (rc <= 0) return total > 0 ? total : rc;
                    total += rc;
                    remaining -= rc;
                    break;
                }
            }
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile linuxFile, FiberTask task)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return AwaitResult.Completed;

            if (_count > 0 || _writersClosed)
                return AwaitResult.Completed;

            if (_readHandle.IsSignaled)
                _readHandle.Reset();
        }

        var result = await _readHandle.WaitInterruptiblyAsync(task);
        return result;
    }

    public override async ValueTask<AwaitResult> WaitForWrite(LinuxFile linuxFile, FiberTask task,
        int minWritableBytes = 1)
    {
        minWritableBytes = Math.Clamp(minWritableBytes, 1, _capacity);

        while (true)
        {
            using (EnterStateScope())
            {
                if (_lifecycleClosed)
                    return AwaitResult.Completed;

                var space = _capacity - _count;
                if (_readersClosed || space >= minWritableBytes)
                    return AwaitResult.Completed;

                // poll(2) documents POLLOUT as "writing is now possible", not "the full
                // pending write fits right now". For pipe(7) atomic writes <= PIPE_BUF we
                // may therefore need to ignore intermediate write-space wakeups until enough
                // capacity accumulates for the requested chunk.
                if (_writeHandle.IsSignaled)
                    _writeHandle.Reset();
            }

            var result = await _writeHandle.WaitInterruptiblyAsync(task);
            if (result == AwaitResult.Interrupted)
                return AwaitResult.Interrupted;
        }
    }

    public int Capacity => _capacity;

    /// <summary>
    ///     Linux tracks pipe fill in page-sized buffers: a pipe with a partially consumed
    ///     buffer is still full for poll(2)/epoll purposes until that buffer is released.
    ///     Report writability only when a whole page-sized slot is free.
    /// </summary>
    private bool HasWritableBuffer() => _capacity - _count >= PipeBuf;

    public int TrySetCapacity(int requested)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed) return -(int)Errno.EBADF;
            if (requested <= 0) return -(int)Errno.EINVAL;
            var size = Math.Clamp((requested + MinPipeCapacity - 1) / MinPipeCapacity * MinPipeCapacity,
                MinPipeCapacity, MaxPipeCapacity);
            if (size < _count) return -(int)Errno.EBUSY;
            _capacity = size;
            if (HasWritableBuffer()) _writeHandle.Set();
            return _capacity;
        }
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        const short POLLERR = 0x0008;
        const short POLLHUP = 0x0010;

        short revents = 0;

        using (EnterStateScope())
        {
            const int O_ACCMODE = 3;
            var mode = (int)linuxFile.Flags & O_ACCMODE;
            var canRead = mode == (int)FileFlags.O_RDONLY || mode == (int)FileFlags.O_RDWR;
            var canWrite = mode == (int)FileFlags.O_WRONLY || mode == (int)FileFlags.O_RDWR;

            if (_lifecycleClosed)
            {
                if (canRead)
                    revents |= POLLHUP;
                if (canWrite)
                    revents |= POLLERR;
                return revents;
            }

            var readWatch = canRead
                ? new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle, _readHandle.Reset)
                : default;
            var writeWatch = canWrite && !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            revents |= QueueReadinessRegistration.ComputeRevents(events, readWatch, writeWatch);
            if (_writersClosed && _count == 0)
                revents &= unchecked((short)~POLLIN);

            if (canRead)
                if (_writersClosed)
                    revents |= POLLHUP;

            if (canWrite)
                if (_readersClosed)
                    revents |= POLLERR;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return null;
    }

    public override IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, HasWritableBuffer, _writeHandle, _writeHandle.Reset)
                : default;

            return QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, _waitQueues.Scheduler, events,
                readWatch, writeWatch);
        }
    }

    public override void Release(LinuxFile linuxFile)
    {
        const int O_ACCMODE = 3;
        var mode = (int)linuxFile.Flags & O_ACCMODE;
        if (mode == (int)FileFlags.O_RDONLY)
        {
            RemoveReader();
        }
        else if (mode == (int)FileFlags.O_RDWR)
        {
            RemoveReader();
            RemoveWriter();
        }
        else
        {
            RemoveWriter();
        }
    }

    protected override void OnEvictCache()
    {
        ReturnHandlesToPool();
        ReturnBufferToPool();
        base.OnEvictCache();
    }

    private void CloseLifecycleOnce()
    {
        if (_lifecycleClosed)
            return;

        _lifecycleClosed = true;

        _count = 0;
        _head = 0;
        _tail = 0;
        _readerCount = 0;
        _writerCount = 0;
        _readersClosed = true;
        _writersClosed = true;
        _readHandle.Reset();
        _writeHandle.Reset();
        ReturnHandlesToPool();
        AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "PipeInode.CloseLifecycleOnce");

        ReturnBufferToPool();
    }

    private void ReturnBufferToPool()
    {
        if (_buffer.Length == 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
        _buffer = Array.Empty<byte>();
    }

    private void ReturnHandlesToPool()
    {
        _waitQueues.ReturnToPool();
    }

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
