using System;
using System.Collections.Concurrent;
using System.Threading;
using Bifrost.Syscalls;
using Bifrost.Core;
using Bifrost.Native; // For Errno
using Task = Bifrost.Core.Task; // For Scheduler

namespace Bifrost.VFS;

public class PipeInode : Inode
{
    private readonly object _lock = new();
    private byte[] _buffer;
    private int _head; // Write position
    private int _tail; // Read position
    private int _count;
    private bool _writersClosed;
    private bool _readersClosed;
    private int _readerCount;
    private int _writerCount;
    private readonly SemaphoreSlim _readEvent = new(0);
    private readonly SemaphoreSlim _writeEvent = new(0);

    private const int BufferSize = 65536; // 64KB pipe buffer

    public PipeInode()
    {
        Type = InodeType.Fifo;
        Mode = 0x1000 | 0x1FF; // FIFO + 777
        _buffer = new byte[BufferSize];
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
        }
    }

    public void RemoveReader()
    {
        bool wakeWriters = false;
        lock (_lock)
        {
            _readerCount--;
            if (_readerCount <= 0)
            {
                _readersClosed = true;
                wakeWriters = true;
            }
        }
        if (wakeWriters) _writeEvent.Release(100); // Wake up all writers
    }

    public void RemoveWriter()
    {
        bool wakeReaders = false;
        lock (_lock)
        {
            _writerCount--;
            if (_writerCount <= 0)
            {
                _writersClosed = true;
                wakeReaders = true;
            }
        }
        if (wakeReaders) _readEvent.Release(100); // Wake up all readers
    }

    public override void Open(File file)
    {
        const int O_ACCMODE = 3;
        if (((int)file.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY)
        {
            AddReader();
        }
        else
        {
            AddWriter();
        }
    }

    public override int Read(File file, Span<byte> buffer, long offset)

    {
        // Pipe ignores offset
        int needed = buffer.Length;
        if (needed == 0) return 0;

        int read = 0;

        while (true)
        {
            bool ready = false;
            bool eof = false;

            lock (_lock)
            {
                if (_count > 0)
                {
                    ready = true;
                    // Read data
                    int available = Math.Min(needed - read, _count);
                    int firstChunk = Math.Min(available, BufferSize - _tail);
                    
                    if (firstChunk > 0)
                    {
                        new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer.Slice(read));
                        _tail = (_tail + firstChunk) % BufferSize;
                        _count -= firstChunk;
                        read += firstChunk;
                        available -= firstChunk;
                    }

                    if (available > 0) // Wrap around
                    {
                        new ReadOnlySpan<byte>(_buffer, _tail, available).CopyTo(buffer.Slice(read));
                        _tail = (_tail + available) % BufferSize;
                        _count -= available;
                        read += available;
                    }
                }
                else if (_writersClosed)
                {
                    eof = true;
                }
            }

            if (ready)
            {
                // We read something.
                _writeEvent.Release(1); // Notify space available
                return read; // Partial read is fine for pipe
            }

            if (eof)
            {
                return read; // Should be 0 if we reached here directly
            }

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            {
                return -(int)Errno.EAGAIN;
            }

            // Block
            var task = Scheduler.GetCurrent();
            if (task != null)
            {
                task.BlockingTask = _readEvent.WaitAsync();
                return 0; // Trigger Yield
            }
            else
            {
                _readEvent.Wait(); // Blocking wait (should not happen in async loop usually)
            }
        }
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        // Pipe ignores offset
        int len = buffer.Length;
        int written = 0;

        while (written < len)
        {
            bool spaceAvailable = false;
            bool brokenPipe = false;

            lock (_lock)
            {
                if (_readersClosed)
                {
                    brokenPipe = true;
                }
                else if (_count < BufferSize)
                {
                    spaceAvailable = true;
                    int space = BufferSize - _count;
                    int toWrite = Math.Min(len - written, space);
                    
                    int firstChunk = Math.Min(toWrite, BufferSize - _head);
                    if (firstChunk > 0)
                    {
                        buffer.Slice(written, firstChunk).CopyTo(new Span<byte>(_buffer, _head, firstChunk));
                        _head = (_head + firstChunk) % BufferSize;
                        _count += firstChunk;
                        written += firstChunk;
                        toWrite -= firstChunk;
                    }

                    if (toWrite > 0)
                    {
                        buffer.Slice(written, toWrite).CopyTo(new Span<byte>(_buffer, _head, toWrite));
                        _head = (_head + toWrite) % BufferSize;
                        _count += toWrite;
                        written += toWrite;
                    }
                }
            }

            if (brokenPipe)
            {
                // Send SIGPIPE to current task?
                return -(int)Errno.EPIPE;
            }

            if (spaceAvailable)
            {
                _readEvent.Release(1); // Notify data available
                continue; // Can write more?
            }

            // Buffer full
            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            {
                return written > 0 ? written : -(int)Errno.EAGAIN;
            }

            var task = Scheduler.GetCurrent();
            if (task != null)
            {
                task.BlockingTask = _writeEvent.WaitAsync();
                return written; // Wait for space
            }
             else
            {
                _writeEvent.Wait();
            }
        }
        return written;
    }

    public override void Release(File file)
    {
        // O_ACCMODE is 3 (O_RDONLY | O_WRONLY | O_RDWR) - but strictly speaking we only care about read vs write intent
        // In Linux pipe, FDs are usually O_RDONLY or O_WRONLY.
        const int O_ACCMODE = 3;
        if (((int)file.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY)
        {
            RemoveReader();
        }
        else
        {
            RemoveWriter();
        }
    }
}
