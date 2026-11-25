using System.Buffers;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const int SpliceFNonBlock = 0x2;
    private const int SpliceFMove = 0x1;
    private const int SpliceFMore = 0x4;
    private const int SpliceFGift = 0x8;
    private const int MaxVmspliceSegs = 1024;

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature

    /// <summary>
    ///     vmsplice(2): copy user pages into a pipe (or pipe data into user memory
    ///     for a pipe read end).
    /// </summary>
    private ValueTask<int> SysVmsplice(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return ValueTask.FromResult(-(int)Errno.EPERM);

        var fd = (int)a1;
        var iovPtr = a2;
        var nrSegs = (long)(uint)a3;
        var flags = (int)a4;

        if (nrSegs < 0 || nrSegs > MaxVmspliceSegs)
            return ValueTask.FromResult(-(int)Errno.EINVAL);
        if ((flags & ~(SpliceFMove | SpliceFNonBlock | SpliceFMore | SpliceFGift)) != 0)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        var file = GetFD(fd);
        if (file == null || file.OpenedInode == null) return ValueTask.FromResult(-(int)Errno.EBADF);
        if (file.IoInode is not PipeInode pipe) return ValueTask.FromResult(-(int)Errno.EBADF);
        if (nrSegs == 0) return ValueTask.FromResult(0);
        var lease = new FileIoLease(file);
        try { return lease.Complete(DoVmsplice(engine, task, file, pipe, iovPtr, nrSegs, flags), task); }
        finally { lease.Dispose(); }
    }

    private async ValueTask<int> DoVmsplice(Engine engine, FiberTask task, LinuxFile file, PipeInode pipe,
        uint iovPtr, long nrSegs, int flags)
    {
        var iovs = ReadIovecs(this, engine, iovPtr, (int)nrSegs, out var iovErr);
        if (iovs == null) return iovErr;
        var rentedIovs = new TaskIoBuffer<Iovec>(iovs);

        try
        {
            var accessMode = file.Flags & FileFlags.O_ACCMODE;
            var writesToPipe = accessMode != FileFlags.O_RDONLY;
            var nonBlock = (flags & SpliceFNonBlock) != 0 || (file.Flags & FileFlags.O_NONBLOCK) != 0;

            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            var rented = new TaskIoBuffer<byte>(buffer);
            try
            {
                var total = 0;
                for (var i = 0; i < (int)nrSegs; i++)
                {
                    var iov = iovs[i];
                    if (iov.Len == 0) continue;
                    var remaining = (int)iov.Len;
                    var baseAddr = iov.BaseAddr;
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(remaining, buffer.Length);
                        if (writesToPipe)
                        {
                            if (!engine.CopyFromUser(baseAddr, buffer.AsSpan(0, chunk)))
                                return total > 0 ? total : -(int)Errno.EFAULT;

                            var written = pipe.WriteFromHost(task, file, buffer.AsSpan(0, chunk), 0);
                            if (written == -(int)Errno.EAGAIN)
                            {
                                if (total > 0) return total;
                                if (nonBlock) return -(int)Errno.EAGAIN;
                                var minWritableBytes = chunk <= PipeInode.PipeBuf ? chunk : 1;
                                var operation = pipe.WaitForWrite(file, task, minWritableBytes);
                                if (!operation.IsCompleted) rentedIovs.TrackPending(task);
                                var wait = await rented.Track(operation, task);
                                if (wait == AwaitResult.Interrupted)
                                    return total > 0 ? total : -(int)Errno.ERESTARTSYS;
                                continue;
                            }

                            if (written == -(int)Errno.EPIPE)
                            {
                                if (total > 0) return total;
                                task.PostSignal((int)Signal.SIGPIPE);
                                return written;
                            }

                            if (written < 0) return total > 0 ? total : written;
                            total += written;
                            baseAddr += (uint)written;
                            remaining -= written;
                            if (written < chunk) return total;
                        }
                        else
                        {
                            var read = pipe.ReadToHost(task, file, buffer.AsSpan(0, chunk), 0);
                            if (read == -(int)Errno.EAGAIN)
                            {
                                if (total > 0) return total;
                                if (nonBlock) return -(int)Errno.EAGAIN;
                                var operation = pipe.WaitForRead(file, task);
                                if (!operation.IsCompleted) rentedIovs.TrackPending(task);
                                var wait = await rented.Track(operation, task);
                                if (wait == AwaitResult.Interrupted)
                                    return total > 0 ? total : -(int)Errno.ERESTARTSYS;
                                continue;
                            }

                            if (read == 0) return total;
                            if (read < 0) return total > 0 ? total : read;
                            if (!engine.CopyToUser(baseAddr, buffer.AsSpan(0, read)))
                                return total > 0 ? total : -(int)Errno.EFAULT;
                            total += read;
                            baseAddr += (uint)read;
                            remaining -= read;
                            if (read < chunk) return total;
                        }
                    }
                }

                return total;
            }
            finally
            {
                rented.Dispose();
            }
        }
        finally
        {
            rentedIovs.Dispose();
        }
    }

    /// <summary>
    ///     copy_file_range(2): copy a byte range between two files without a trip
    ///     through user space.
    /// </summary>
    private ValueTask<int> SysCopyFileRange(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return ValueTask.FromResult(-(int)Errno.EPERM);
        if (a6 != 0) return ValueTask.FromResult(-(int)Errno.EINVAL);
        var fileIn = GetFD((int)a1);
        var fileOut = GetFD((int)a3);
        if (fileIn?.OpenedInode == null || fileOut?.OpenedInode == null)
            return ValueTask.FromResult(-(int)Errno.EBADF);
        var lease = new FileIoLease(fileIn, fileOut);
        try { return lease.Complete(CopyFileRangeAsync(engine, task, fileIn, fileOut, a2, a4, a5), task); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<int> CopyFileRangeAsync(Engine engine, FiberTask task, LinuxFile fileIn,
        LinuxFile fileOut, uint offInPtr, uint offOutPtr, uint len)
    {
        if (fileIn.IsPathOnly || fileOut.IsPathOnly) return -(int)Errno.EBADF;

        if ((fileIn.Flags & FileFlags.O_ACCMODE) == FileFlags.O_WRONLY) return -(int)Errno.EBADF;
        if ((fileOut.Flags & FileFlags.O_ACCMODE) == FileFlags.O_RDONLY) return -(int)Errno.EBADF;
        if ((fileOut.Flags & FileFlags.O_APPEND) != 0) return -(int)Errno.EBADF;

        long readOffset;
        long writeOffset;
        if (offInPtr != 0)
        {
            var buf = new byte[8];
            if (!engine.CopyFromUser(offInPtr, buf)) return -(int)Errno.EFAULT;
            readOffset = BitConverter.ToInt64(buf);
        }
        else
        {
            readOffset = fileIn.Position;
        }

        if (offOutPtr != 0)
        {
            var buf = new byte[8];
            if (!engine.CopyFromUser(offOutPtr, buf)) return -(int)Errno.EFAULT;
            writeOffset = BitConverter.ToInt64(buf);
        }
        else
        {
            writeOffset = fileOut.Position;
        }

        bool OffsetsAreValid() => readOffset >= 0 && writeOffset >= 0 &&
            (!ReferenceEquals(fileIn.OpenedInode, fileOut.OpenedInode) ||
             Math.Abs(readOffset - writeOffset) >= len);

        if (!OffsetsAreValid()) return -(int)Errno.EINVAL;

        if (len == 0) return 0;
        var prepare = await fileOut.IoInode!.PrepareWriteAsync(task, fileOut);
        if (prepare < 0) return prepare;
        if (offInPtr == 0) readOffset = fileIn.Position;
        if (offOutPtr == 0) writeOffset = fileOut.Position;
        if (!OffsetsAreValid()) return -(int)Errno.EINVAL;

        const int bufferSize = 32 * 1024;
        var asyncRead = fileIn.IoInode!.SupportsAsyncRead;
        var buffer = asyncRead ? new byte[bufferSize] : ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var total = 0;
            var remaining = (long)len;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(remaining, buffer.Length);
                var read = await fileIn.IoInode!.ReadToHostAsync(task, fileIn, buffer.AsMemory(0, chunk), readOffset);
                if (read < 0)
                {
                    if (total == 0) return read;
                    break;
                }
                if ((offInPtr == 0 && readOffset != fileIn.Position) ||
                    (offOutPtr == 0 && writeOffset != fileOut.Position))
                {
                    if (total > 0) break;
                    if (offInPtr == 0) readOffset = fileIn.Position;
                    if (offOutPtr == 0) writeOffset = fileOut.Position;
                    if (!OffsetsAreValid()) return -(int)Errno.EINVAL;
                    continue;
                }
                if (read == 0) break;

                var written = fileOut.IoInode!.WriteFromHost(task, fileOut, buffer.AsSpan(0, read),
                    writeOffset);
                if (written < 0)
                {
                    if (total == 0) return written;
                    break;
                }
                if (written == 0) break;

                total += written;
                readOffset += written;
                writeOffset += written;
                remaining -= written;
                if (offInPtr == 0) fileIn.Position = readOffset;
                if (offOutPtr == 0) fileOut.Position = writeOffset;
                if (written < read) break;
            }

            if (offInPtr != 0)
                engine.CopyToUser(offInPtr, BitConverter.GetBytes(readOffset));
            if (offOutPtr != 0)
                engine.CopyToUser(offOutPtr, BitConverter.GetBytes(writeOffset));

            return total;
        }
        finally
        {
            if (!asyncRead) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     close_range(2): close (or mark close-on-exec) every descriptor in a range.
    /// </summary>
    private async ValueTask<int> SysCloseRange(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        const uint CloseRangeUnshare = 0x2;
        const uint CloseRangeCloexec = 0x4;

        var first = a1;
        var last = a2;
        var flags = a3;

        if (first > last) return -(int)Errno.EINVAL;
        if ((flags & ~(CloseRangeUnshare | CloseRangeCloexec)) != 0) return -(int)Errno.EINVAL;

        var targets = new List<int>();
        foreach (var fd in FDs.Keys)
        {
            var fdValue = (uint)fd;
            if (fdValue >= first && fdValue <= last)
                targets.Add(fd);
        }

        var markCloexec = (flags & CloseRangeCloexec) != 0;
        if ((flags & CloseRangeUnshare) != 0)
            UnshareFdTable(engine.Owner as FiberTask);

        foreach (var fd in targets)
        {
            if (markCloexec)
                SetFdCloseOnExec(fd, true);
            else
                FreeFD(fd);
        }

        return 0;
    }

#pragma warning restore CS1998
}
