using System;
using System.Collections.Concurrent;
using System.Threading;
using Bifrost.Syscalls;
using Bifrost.Core;
using Bifrost.Native; // For Errno


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
    
    // Notification handles
    private readonly Bifrost.Core.WaitHandle _readHandle = new();
    private readonly Bifrost.Core.WaitHandle _writeHandle = new();

    private const int BufferSize = 65536; // 64KB pipe buffer

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
        lock (_lock)
        {
            if (_count > 0)
            {
                // Read data
                int available = Math.Min(buffer.Length, _count);
                int firstChunk = Math.Min(available, BufferSize - _tail);
                
                if (firstChunk > 0)
                {
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer.Slice(0, firstChunk));
                    _tail = (_tail + firstChunk) % BufferSize;
                }

                if (available > firstChunk)
                {
                    int secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, _tail, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                    _tail = (_tail + secondChunk) % BufferSize;
                }
                
                _count -= available;
                
                // Update handles
                if (_count == 0) _readHandle.Reset();
                _writeHandle.Set(); // Space available

                return available;
            }
            
            if (_writersClosed)
            {
                return 0; // EOF
            }
            
            return -(int)Errno.EAGAIN;
        }
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        lock (_lock)
        {
            if (_readersClosed)
            {
                // Broken pipe
                 // Send SIGPIPE to current task?
                return -(int)Errno.EPIPE;
            }

            int space = BufferSize - _count;
            if (space > 0)
            {
                int toWrite = Math.Min(buffer.Length, space);
                int firstChunk = Math.Min(toWrite, BufferSize - _head);

                if (firstChunk > 0)
                {
                    buffer.Slice(0, firstChunk).CopyTo(new Span<byte>(_buffer, _head, firstChunk));
                    _head = (_head + firstChunk) % BufferSize;
                }

                if (toWrite > firstChunk)
                {
                    int secondChunk = toWrite - firstChunk;
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

    public override async ValueTask WaitForRead(File file)
    {
        // Must await the handle
        // TODO: Handle cancellation/interruption?
        await _readHandle;
    }

    public override async ValueTask WaitForWrite(File file)
    {
         await _writeHandle;
    }

    public override void Release(File file)
    {
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
