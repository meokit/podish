using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

// For Errno


namespace Fiberish.VFS;

public class PipeInode : Inode, ITaskWaitSource, IDispatcherWaitSource
{
    private const int BufferSize = 65536; // 64KB pipe buffer
    public const int PipeBuf = 4096;
    private readonly byte[] _buffer;

    // Notification handles
    private readonly WaitHandle _readHandle;
    private readonly WaitHandle _writeHandle;
    private int _count;
    private int _head; // Write position
    private int _readerCount;
    private bool _readersClosed;
    private int _tail; // Read position
    private int _writerCount;
    private bool _writersClosed;

    public PipeInode(KernelScheduler scheduler)
    {
        _readHandle = new WaitHandle(scheduler);
        _writeHandle = new WaitHandle(scheduler);
        Type = InodeType.Fifo;
        Mode = 0x1000 | 0x1FF; // FIFO + 777
        _buffer = new byte[BufferSize];
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
            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, () => _count < BufferSize, _writeHandle, _writeHandle.Reset)
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
            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, () => _count < BufferSize, _writeHandle, _writeHandle.Reset)
                : default;

            return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch, writeWatch);
        }
    }

    public bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, () => _count < BufferSize, _writeHandle, _writeHandle.Reset)
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
            var readWatch = new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle,
                _readHandle.Reset);
            var writeWatch = !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, () => _count < BufferSize, _writeHandle, _writeHandle.Reset)
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

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        using (EnterStateScope())
        {
            if (_count > 0)
            {
                // Read data
                var available = Math.Min(buffer.Length, _count);
                var firstChunk = Math.Min(available, BufferSize - _tail);

                if (firstChunk > 0)
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer[..firstChunk]);

                if (available > firstChunk)
                {
                    var secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, 0, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                }

                _tail = (_tail + available) % BufferSize;
                _count -= available;

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
            if (_count > 0)
            {
                var available = Math.Min(buffer.Length, _count);
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

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        using (EnterStateScope())
        {
            if (_readersClosed)
                // Broken pipe
                // Send SIGPIPE to current task?
                return -(int)Errno.EPIPE;

            var space = BufferSize - _count;
            if (space > 0)
            {
                // Linux pipe(7): blocking writes of n <= PIPE_BUF bytes must remain atomic,
                // so we only report progress here when the pipe can accept the whole chunk.
                // Non-blocking writes of n <= PIPE_BUF bytes return EAGAIN instead of partial progress.
                if (buffer.Length <= PipeBuf && space < buffer.Length)
                    return -(int)Errno.EAGAIN;

                var toWrite = Math.Min(buffer.Length, space);
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

                // Update handles
                if (_count == BufferSize) _writeHandle.Reset();
                _readHandle.Set(); // Data available

                return toWrite;
            }

            return -(int)Errno.EAGAIN;
        }
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile linuxFile, FiberTask task)
    {
        using (EnterStateScope())
        {
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
        minWritableBytes = Math.Clamp(minWritableBytes, 1, BufferSize);

        while (true)
        {
            using (EnterStateScope())
            {
                var space = BufferSize - _count;
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
            var readWatch = canRead
                ? new QueueReadinessWatch(POLLIN, () => _count > 0 || _writersClosed, _readHandle, _readHandle.Reset)
                : default;
            var writeWatch = canWrite && !_readersClosed
                ? new QueueReadinessWatch(POLLOUT, () => _count < BufferSize, _writeHandle, _writeHandle.Reset)
                : default;

            revents |= QueueReadinessRegistration.ComputeRevents(events, readWatch, writeWatch);

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

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}