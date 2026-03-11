using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

// For Errno


namespace Fiberish.VFS;

public class PipeInode : Inode
{
    private const int BufferSize = 65536; // 64KB pipe buffer
    private readonly byte[] _buffer;

    // Notification handles
    private readonly WaitHandle _readHandle = new();
    private readonly WaitHandle _writeHandle = new();
    private int _count;
    private int _head; // Write position
    private int _readerCount;
    private bool _readersClosed;
    private int _tail; // Read position
    private int _writerCount;
    private bool _writersClosed;

    public PipeInode()
    {
        Type = InodeType.Fifo;
        Mode = 0x1000 | 0x1FF; // FIFO + 777
        _buffer = new byte[BufferSize];
        // Initially writable (empty)
        _writeHandle.Set();
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        KernelScheduler.Current?.AssertSchedulerThread(caller);
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

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
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

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
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
                // Atomic write guarantee: writes <= PIPE_BUF must not be interleaved/partial
                const int PIPE_BUF = 4096;
                if (buffer.Length <= PIPE_BUF && space < buffer.Length)
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

    public override async ValueTask WaitForRead(LinuxFile linuxFile)
    {
        // Must await the handle
        // TODO: Handle cancellation/interruption?
        await _readHandle;
    }

    public override async ValueTask WaitForWrite(LinuxFile linuxFile)
    {
        await _writeHandle;
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

            if (canRead)
            {
                if (_writersClosed)
                    revents |= POLLHUP;

                if ((events & POLLIN) != 0)
                {
                    if (_count > 0)
                        revents |= POLLIN;
                    else if (_writersClosed)
                        // EOF - no writers left. Keep POLLIN for compatibility with existing select logic.
                        revents |= POLLIN;
                }
            }

            if (canWrite)
            {
                if (_readersClosed)
                    revents |= POLLERR;
                else if ((events & POLLOUT) != 0 && _count < BufferSize)
                    revents |= POLLOUT;
            }
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            if ((events & POLLIN) != 0)
            {
                // Register for read availability
                _readHandle.Register(callback);
                registered = true;
            }

            if ((events & POLLOUT) != 0)
            {
                // Register for write space availability
                _writeHandle.Register(callback);
                registered = true;
            }
        }

        return registered;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registrations = new List<IDisposable>(2);

        using (EnterStateScope())
        {
            if ((events & POLLIN) != 0)
            {
                var reg = _readHandle.RegisterCancelable(callback);
                if (reg != null) registrations.Add(reg);
            }

            if ((events & POLLOUT) != 0)
            {
                var reg = _writeHandle.RegisterCancelable(callback);
                if (reg != null) registrations.Add(reg);
            }
        }

        return registrations.Count switch
        {
            0 => null,
            1 => registrations[0],
            _ => new CompositeDisposable(registrations)
        };
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

    private sealed class CompositeDisposable : IDisposable
    {
        private List<IDisposable>? _items;

        public CompositeDisposable(List<IDisposable> items)
        {
            _items = items;
        }

        public void Dispose()
        {
            var items = Interlocked.Exchange(ref _items, null);
            if (items == null) return;
            foreach (var item in items) item.Dispose();
        }
    }
}