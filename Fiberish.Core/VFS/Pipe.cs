using Fiberish.Native;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

// For Errno


namespace Fiberish.VFS;

public class PipeInode : Inode
{
    private const int BufferSize = 65536; // 64KB pipe buffer
    private readonly byte[] _buffer;
    private readonly object _lock = new();

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

    public void AddReader()
    {
        lock (_lock)
        {
            _readerCount++;
            _readersClosed = false;
        }
    }

    public void AddWriter()
    {
        lock (_lock)
        {
            _writerCount++;
            _writersClosed = false;
            // If writers were closed, reopening might affect read handle? No.
        }
    }

    public void RemoveReader()
    {
        lock (_lock)
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
        lock (_lock)
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
        if (((int)linuxFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY)
            AddReader();
        else
            AddWriter();
    }

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        lock (_lock)
        {
            if (_count > 0)
            {
                // Read data
                var available = Math.Min(buffer.Length, _count);
                var firstChunk = Math.Min(available, BufferSize - _tail);

                if (firstChunk > 0)
                {
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer[..firstChunk]);
                    _tail = (_tail + firstChunk) % BufferSize;
                }

                if (available > firstChunk)
                {
                    var secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, _tail, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                    _tail = (_tail + secondChunk) % BufferSize;
                }

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

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        lock (_lock)
        {
            if (_readersClosed)
                // Broken pipe
                // Send SIGPIPE to current task?
                return -(int)Errno.EPIPE;

            var space = BufferSize - _count;
            if (space > 0)
            {
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

        lock (_lock)
        {
            const int O_ACCMODE = 3;
            var mode = (int)linuxFile.Flags & O_ACCMODE;

            if ((events & POLLIN) != 0 && (mode == (int)FileFlags.O_RDONLY || mode == (int)FileFlags.O_RDWR))
            {
                // Check if there's data to read or EOF
                if (_count > 0)
                {
                    revents |= POLLIN;
                }
                else if (_writersClosed)
                {
                    // EOF - no writers left, return POLLHUP
                    revents |= POLLHUP;
                }
            }

            if ((events & POLLOUT) != 0 && (mode == (int)FileFlags.O_WRONLY || mode == (int)FileFlags.O_RDWR))
            {
                // Check if there's space to write or broken pipe
                if (_readersClosed)
                {
                    revents |= POLLERR;
                }
                else if (_count < BufferSize)
                {
                    revents |= POLLOUT;
                }
            }
        }

        return revents;
    }

    public override void Release(LinuxFile linuxFile)
    {
        const int O_ACCMODE = 3;
        if (((int)linuxFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY)
            RemoveReader();
        else
            RemoveWriter();
    }
}