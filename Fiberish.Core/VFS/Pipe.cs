using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

// For Errno


namespace Fiberish.VFS;

public class PipeQuotaExceededException : Exception
{
    public PipeQuotaExceededException() : base("Pipe user page hard limit exceeded")
    {
    }
}

public class PipeInode : Inode, ITaskWaitSource, IDispatcherWaitSource, IDispatcherEdgeWaitSource,
    IPollNotificationSource
{
    private const int DefaultCapacity = 65536; // 64KB default pipe buffer
    public const int PipeBuf = 4096;
    private int _capacity;
    private byte[] _buffer;
    private readonly bool _packetMode;
    private readonly Queue<int> _packetLengths = new();
    private int _packetReadOffset;
    private HashSet<LinuxFile>? _asyncReaders;

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
    private int _pipeAccountUid = -1;
    private int _pipeAccountPages;
    private bool _pipeAccountReleased;
    private readonly Inode? _namedOwner;
    private AsyncWaitQueue? _openQueue;
    private ulong _readerGeneration;
    private ulong _writerGeneration;

    public PipeInode(KernelScheduler scheduler, bool packetMode = false)
        : this(scheduler, packetMode, null)
    {
    }

    internal PipeInode(KernelScheduler scheduler, bool packetMode, Inode? namedOwner)
    {
        _namedOwner = namedOwner;
        _packetMode = packetMode;
        var process = scheduler.CurrentTask?.Process;
        var isUnpriv = process != null && process.EUID != 0 &&
                       !process.HasEffectiveCapability(Process.CapabilitySysResource);
        var initialCap = DefaultCapacity;
        if (isUnpriv && scheduler.PipeMaxSize < initialCap)
            initialCap = Math.Max(4096, NormalizePipeCapacity(scheduler.PipeMaxSize));

        // Reserve the user page budget before renting pooled wait queues or a buffer:
        // the hard limit is a normal rejection path, and partially constructed pipes
        // must not leak scheduler resources.
        var accountUid = -1;
        var accountPages = 0;
        if (isUnpriv)
        {
            accountUid = process!.UID;
            var requestedPages = Math.Max(1, initialCap / PipeBuf);
            accountPages = scheduler.ReservePipePages(accountUid, requestedPages);
            initialCap = accountPages * PipeBuf;
        }

        PooledReadWriteWaitQueues waitQueues;
        try
        {
            waitQueues = new PooledReadWriteWaitQueues(scheduler);
        }
        catch
        {
            if (accountUid >= 0 && accountPages > 0)
                scheduler.ReleasePipePages(accountUid, accountPages);
            throw;
        }

        _waitQueues = waitQueues;
        _readHandle = waitQueues.ReadQueue;
        _writeHandle = waitQueues.WriteQueue;
        _pipeAccountUid = accountUid;
        _pipeAccountPages = accountPages;
        _capacity = initialCap;
        Type = InodeType.Fifo;
        Mode = 0x1000 | 0x1FF; // FIFO + 777
        if (namedOwner == null)
            AnonymousInodeLifecycle.Initialize(this, "PipeInode.ctor");
        else
        {
            SuperBlock = namedOwner.SuperBlock;
            _readersClosed = _writersClosed = true;
        }
        try
        {
            _buffer = ArrayPool<byte>.Shared.Rent(_capacity);
        }
        catch
        {
            ReleasePipeAccount();
            _waitQueues.ReturnToPool();
            throw;
        }
        // Initially writable (empty)
        _writeHandle.Set();
    }

    private static int NormalizePipeCapacity(int requested)
    {
        if (requested <= 4096)
            return 4096;
        var rounded = BitOperations.RoundUpToPowerOf2((uint)requested);
        return rounded > (1u << 30) ? -1 : (int)rounded;
    }

    public int GetPipeSize()
    {
        using (EnterStateScope())
        {
            return _capacity;
        }
    }

    public int SetPipeSize(int requestedSize, bool isPrivileged)
    {
        if (requestedSize < 0)
            return -(int)Errno.EINVAL;

        // Round on a wide axis: INT_MAX rounds to 2 GiB, not an invalid negative size.
        // Linux applies the unprivileged limit before attempting the allocation.
        var targetBytes = (long)BitOperations.RoundUpToPowerOf2((uint)Math.Max(requestedSize, PipeBuf));
        var pipeMax = _waitQueues.Scheduler.PipeMaxSize;
        if (!isPrivileged && targetBytes > pipeMax)
            return -(int)Errno.EPERM;
        // This backend needs a contiguous managed byte array, unlike Linux's page-slot ring.
        if (targetBytes > Array.MaxLength)
            return -(int)Errno.ENOMEM;
        var target = (int)targetBytes;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return -(int)Errno.EBADF;
            if (target < _count)
                return -(int)Errno.EBUSY;
            if (target == _capacity)
                return _capacity;

            var targetPages = target / PipeBuf;
            var pageDelta = targetPages - _pipeAccountPages;
            if (pageDelta > 0 && _pipeAccountUid >= 0 &&
                !_waitQueues.Scheduler.TryReservePipePages(_pipeAccountUid, pageDelta))
                return -(int)Errno.EPERM;

            byte[] newBuffer;
            try { newBuffer = ArrayPool<byte>.Shared.Rent(target); }
            catch (OutOfMemoryException)
            {
                if (pageDelta > 0 && _pipeAccountUid >= 0)
                    _waitQueues.Scheduler.ReleasePipePages(_pipeAccountUid, pageDelta);
                return -(int)Errno.ENOMEM;
            }
            if (_count > 0)
            {
                var firstChunk = Math.Min(_count, _capacity - _tail);
                new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(newBuffer.AsSpan(0, firstChunk));
                if (_count > firstChunk)
                {
                    var secondChunk = _count - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, 0, secondChunk).CopyTo(newBuffer.AsSpan(firstChunk, secondChunk));
                }
            }

            if (_buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
            _buffer = newBuffer;
            _capacity = target;
            if (pageDelta < 0 && _pipeAccountUid >= 0)
                _waitQueues.Scheduler.ReleasePipePages(_pipeAccountUid, -pageDelta);
            _pipeAccountPages = targetPages;
            _tail = 0;
            _head = _count % target;
            if (HasWritableBuffer())
                _writeHandle.Set();
            else
                _writeHandle.Reset();
            return _capacity;
        }
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        if (_namedOwner != null)
            return RegisterNamedWait(linuxFile, null, callback, events, false) != null;
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Pipe readiness wait requires an explicit scheduler.");
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return false;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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

    IDisposable IPollNotificationSource.SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher,
        Func<short, bool> notify, short interests, bool exclusive)
    {
        // A one-way end needs terminal notifications even with an empty mask.
        // RDWR supplies its own reader/writer, so its unused direction cannot
        // acquire HUP/ERR and need not publish irrelevant epoll notifications.
        var mode = file.Flags & FileFlags.O_ACCMODE;
        var read = mode != FileFlags.O_WRONLY &&
                   (mode != FileFlags.O_RDWR || (interests & LinuxConstants.POLLIN) != 0)
            ? _readHandle.SubscribePoll(dispatcher, notify, interests, exclusive) : null;
        var write = mode != FileFlags.O_RDONLY &&
                    (mode != FileFlags.O_RDWR || (interests & LinuxConstants.POLLOUT) != 0)
            ? _writeHandle.SubscribePoll(dispatcher, notify, interests, exclusive) : null;
        return new PollSubscriptions(read, write);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, null, callback, events, false);
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Pipe readiness wait requires an explicit scheduler.");
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, null, callback, events, true);
        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, task, callback, events, false) != null;
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        var registered = false;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return false;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, task, callback, events, false);
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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
            _readerGeneration++;
            _readersClosed = false;
            if (_namedOwner != null)
            {
                _openQueue?.Signal();
                _writeHandle.Signal();
            }
        }
    }

    public void AddWriter()
    {
        using (EnterStateScope())
        {
            _writerCount++;
            _writerGeneration++;
            _writersClosed = false;
            if (_namedOwner != null)
            {
                if (_count == 0) _readHandle.Reset();
                _openQueue?.Signal();
            }
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

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
    {
        if (request == LinuxConstants.FIONREAD)
        {
            int available;
            using (EnterStateScope())
            {
                available = _lifecycleClosed ? 0 : _count;
            }

            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, available);
            return task.CPU.CopyToUser(arg, buf) ? 0 : -(int)Errno.EFAULT;
        }

        return base.Ioctl(linuxFile, task, request, arg);
    }

    public override void Open(LinuxFile linuxFile)
    {
        if (_namedOwner == null) base.Open(linuxFile);
        using (EnterStateScope())
        {
            RefreshAsyncReader(linuxFile);
        }

        const int O_ACCMODE = 3;
        var mode = (int)linuxFile.Flags & O_ACCMODE;
        if (_namedOwner != null)
        {
            // One snapshot serves open rendezvous and the reader's initial-HUP
            // suppression. A reader opened with a live writer already has history.
            linuxFile.FifoPeerGeneration = mode == (int)FileFlags.O_RDONLY
                ? _writerCount == 0 ? _writerGeneration : ulong.MaxValue
                : mode == (int)FileFlags.O_WRONLY ? _readerGeneration : ulong.MaxValue;
        }
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

    internal bool HasReaders => _readerCount != 0;

    internal void ReleaseIfUnopened()
    {
        if (_namedOwner != null && _readerCount == 0 && _writerCount == 0) CloseLifecycleOnce();
    }

    internal bool NeedsOpenPeer(LinuxFile file)
    {
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return false;
        return (file.Flags & FileFlags.O_ACCMODE) switch
        {
            FileFlags.O_RDONLY => _writerCount == 0 && _writerGeneration == file.FifoPeerGeneration,
            FileFlags.O_WRONLY => _readerCount == 0 && _readerGeneration == file.FifoPeerGeneration,
            _ => false
        };
    }

    internal async ValueTask<int> WaitForOpenPeer(LinuxFile file, FiberTask task)
    {
        while (NeedsOpenPeer(file))
        {
            if (task.Exited || task.IsRetiring || task.HasInterruptingPendingSignal())
                return -(int)Errno.ERESTARTSYS;
            var queue = _openQueue ??= _waitQueues.Scheduler.RentAsyncWaitQueue();
            queue.Reset();
            if (await queue.WaitInterruptiblyAsync(task) == AwaitResult.Interrupted)
                return -(int)Errno.ERESTARTSYS;
        }
        return 0;
    }

    private bool HasReadEvent(LinuxFile file) => _count != 0 ||
        (_writersClosed && (_namedOwner == null || file.FifoPeerGeneration != _writerGeneration));

    private IDisposable? RegisterNamedWait(LinuxFile file, FiberTask? task, Action callback, short events, bool edge)
    {
        const short input = 1, output = 4, error = 8, hup = 16;
        var mode = file.Flags & FileFlags.O_ACCMODE;
        var read = mode != FileFlags.O_WRONLY ? new QueueReadinessWatch((short)(input | hup),
            () => ((events & input) != 0 && _count > 0) ||
                  (_writersClosed && file.FifoPeerGeneration != _writerGeneration),
            _readHandle, _readHandle.Reset) : default;
        var write = mode != FileFlags.O_RDONLY ? new QueueReadinessWatch((short)(output | error),
            () => _readersClosed || ((events & output) != 0 && HasWritableBuffer()),
            _writeHandle, _writeHandle.Reset) : default;
        var observe = (short)(events | error | hup);
        if (task != null)
            return edge ? QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, task, observe, read, write)
                : QueueReadinessRegistration.RegisterHandle(callback, task, observe, read, write);
        return edge ? QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, _waitQueues.Scheduler, observe, read, write)
            : QueueReadinessRegistration.RegisterHandle(callback, _waitQueues.Scheduler, observe, read, write);
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
                var consumed = _packetMode ? _packetLengths.Peek() - _packetReadOffset : Math.Min(buffer.Length, _count);
                var available = Math.Min(buffer.Length, consumed);
                var firstChunk = Math.Min(available, _capacity - _tail);

                if (firstChunk > 0)
                    new ReadOnlySpan<byte>(_buffer, _tail, firstChunk).CopyTo(buffer[..firstChunk]);

                if (available > firstChunk)
                {
                    var secondChunk = available - firstChunk;
                    new ReadOnlySpan<byte>(_buffer, 0, secondChunk).CopyTo(buffer.Slice(firstChunk, secondChunk));
                }

                // A short packet read discards the unread tail of that packet.
                _tail = (_tail + consumed) % _capacity;
                _count -= consumed;
                if (_packetMode)
                {
                    _packetLengths.Dequeue();
                    _packetReadOffset = 0;
                }

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
                var available = Math.Min(buffer.Length, _packetMode ? _packetLengths.Peek() - _packetReadOffset : _count);
                var firstChunk = Math.Min(available, _capacity - _tail);

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

    // splice/sendfile advance by accepted bytes, not read(2)'s packet-tail discard.
    internal void ConsumeSpliced(int length)
    {
        using var scope = EnterStateScope();
        if (length < 0 || length > _count) throw new ArgumentOutOfRangeException(nameof(length));
        if (_packetMode)
        {
            var remaining = length;
            while (remaining > 0)
            {
                var packetBytes = _packetLengths.Peek() - _packetReadOffset;
                if (remaining < packetBytes)
                {
                    _packetReadOffset += remaining;
                    break;
                }
                remaining -= packetBytes;
                _packetLengths.Dequeue();
                _packetReadOffset = 0;
            }
        }
        _tail = (_tail + length) % _capacity;
        _count -= length;
        if (_count == 0) _readHandle.Reset();
        _writeHandle.Set();
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
                var firstChunk = Math.Min(toWrite, _capacity - _head);

                if (firstChunk > 0)
                {
                    buffer[..firstChunk].CopyTo(new Span<byte>(_buffer, _head, firstChunk));
                    _head = (_head + firstChunk) % _capacity;
                }

                if (toWrite > firstChunk)
                {
                    var secondChunk = toWrite - firstChunk;
                    buffer.Slice(firstChunk, secondChunk).CopyTo(new Span<byte>(_buffer, _head, secondChunk));
                    _head = (_head + secondChunk) % _capacity;
                }

                _count += toWrite;
                if (_packetMode) _packetLengths.Enqueue(toWrite);

                // Update handles
                if (_count >= _capacity) _writeHandle.Reset();
                _readHandle.Set(); // Data available
                NotifyAsyncReadersLocked();

                return toWrite;
            }

            return -(int)Errno.EAGAIN;
        }
    }

    private void NotifyAsyncReadersLocked()
    {
        const int O_ASYNC = 0x2000;
        const int O_ACCMODE = 3;
        if (_asyncReaders == null || _asyncReaders.Count == 0)
            return;

        var scheduler = _waitQueues.Scheduler;
        foreach (var file in _asyncReaders)
        {
            if (((int)file.Flags & O_ASYNC) == 0)
                continue;
            var mode = (int)file.Flags & O_ACCMODE;
            if (mode == (int)FileFlags.O_WRONLY)
                continue;

            var ownerId = file.AsyncOwnerId;
            if (ownerId <= 0)
                continue;

            var sig = file.AsyncSignal > 0 ? file.AsyncSignal : (int)Signal.SIGIO;
            var info = new SigInfo { Signo = sig, Code = -4 /* SI_ASYNCIO */, Pid = 0, Uid = 0 };
            switch (file.AsyncOwnerType)
            {
                case 0: // F_OWNER_TID
                {
                    var targetTask = scheduler.GetTask(ownerId);
                    if (targetTask != null)
                        targetTask.PostSignal(sig);
                    break;
                }
                case 1: // F_OWNER_PID
                    scheduler.SignalProcessInfo(ownerId, sig, info);
                    break;
                case 2: // F_OWNER_PGRP
                    foreach (var proc in scheduler.GetProcessesSnapshot())
                        if (proc.PGID == ownerId)
                            scheduler.SignalProcessInfo(proc.TGID, sig, info);
                    break;
            }
        }
    }

    internal void RefreshAsyncReader(LinuxFile file)
    {
        if (((int)file.Flags & 0x2000) != 0 && (file.Flags & FileFlags.O_ACCMODE) != FileFlags.O_WRONLY)
            (_asyncReaders ??= new HashSet<LinuxFile>(ReferenceEqualityComparer.Instance)).Add(file);
        else
            _asyncReaders?.Remove(file);
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
        // PIPE_BUF applies to the whole writev, including guest page boundaries.
        // Keep the common single-page/scalar path direct; gather only when an
        // atomic record would otherwise be split by the generic I/O loop.
        long total = 0;
        foreach (var iov in iovs)
        {
            total += iov.Len;
            if (total > PipeBuf) break;
        }
        if (total is > 0 and <= PipeBuf && (iovs.Count > 1 ||
            (iovs[0].BaseAddr & LinuxConstants.PageOffsetMask) + iovs[0].Len > PipeBuf))
            return WriteAtomicV(engine, file, task, iovs, (int)total, flags);
        return WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    private ValueTask<int> WriteAtomicV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, int length, int flags)
    {
        if (_readersClosed)
        {
            task?.PostSignal((int)Signal.SIGPIPE);
            return ValueTask.FromResult(-(int)Errno.EPIPE);
        }
        if (GetWritableBytes() >= length)
            return ValueTask.FromResult(WriteAtomicVReady(engine, file, task, iovs, length));
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 8) != 0 || task == null)
            return ValueTask.FromResult(-(int)Errno.EAGAIN);
        return WriteAtomicVSlow(engine, file, task, iovs, length, flags);
    }

    private async ValueTask<int> WriteAtomicVSlow(Engine engine, LinuxFile file, FiberTask task,
        ArraySegment<Iovec> iovs, int length, int flags)
    {
        while (!_readersClosed && GetWritableBytes() < length)
            if (await WaitForWrite(file, task, length) == AwaitResult.Interrupted)
                return -(int)Errno.ERESTARTSYS;
        return await WriteAtomicV(engine, file, task, iovs, length, flags);
    }

    private int WriteAtomicVReady(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, int length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var copied = 0;
            foreach (var iov in iovs)
            {
                uint done = 0;
                while (done < iov.Len)
                {
                    var address = iov.BaseAddr + done;
                    var chunk = (int)Math.Min(iov.Len - done,
                        LinuxConstants.PageSize - (address & LinuxConstants.PageOffsetMask));
                    if (!engine.CopyFromUser(address, buffer.AsSpan(copied, chunk)))
                        return -(int)Errno.EFAULT;
                    done += (uint)chunk;
                    copied += chunk;
                }
            }
            return WriteFromHost(task, file, buffer.AsSpan(0, copied), 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    public int Capacity => GetPipeSize();

    public bool IsPacketMode => _packetMode;

    public int GetWritableBytes()
    {
        using (EnterStateScope())
        {
            // A closed lifecycle or reader must reach WriteFromHost so it can
            // report EPIPE/SIGPIPE instead of being mistaken for a full pipe.
            if (_lifecycleClosed || _readersClosed) return _capacity;
            return Math.Max(0, _capacity - _count);
        }
    }

    /// <summary>
    ///     Linux tracks pipe fill in page-sized buffers: a pipe with a partially consumed
    ///     buffer is still full for poll(2)/epoll purposes until that buffer is released.
    ///     Report writability only when a whole page-sized slot is free.
    /// </summary>
    private bool HasWritableBuffer() => _capacity - _count >= PipeBuf;

    public int TrySetCapacity(int requested) => SetPipeSize(requested,
        _waitQueues.Scheduler.CurrentTask?.Process.HasEffectiveCapability(Process.CapabilitySysResource) ?? false);

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

            // Sampling readiness is a hot path, not a wait registration: do not
            // allocate predicate/reset delegates or a closure on every poll.
            if (canRead && _count > 0 && (events & POLLIN) != 0) revents |= POLLIN;
            if (canWrite && !_readersClosed && HasWritableBuffer() && (events & POLLOUT) != 0)
                revents |= POLLOUT;

            if (canRead)
                if (_writersClosed && (_namedOwner == null || linuxFile.FifoPeerGeneration != _writerGeneration))
                    revents |= POLLHUP;

            if (canWrite)
                if (_readersClosed)
                    revents |= POLLERR;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, null, callback, events, false) != null;
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, null, callback, events, false);
        return null;
    }

    public override IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        if (_namedOwner != null) return RegisterNamedWait(linuxFile, null, callback, events, true);
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            var readWatch = new QueueReadinessWatch(POLLIN, () => HasReadEvent(linuxFile), _readHandle,
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
        if (_namedOwner == null) base.Release(linuxFile);
        using (EnterStateScope())
        {
            _asyncReaders?.Remove(linuxFile);
        }

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
        _asyncReaders?.Clear();
        _packetLengths.Clear();
        _packetReadOffset = 0;
        _head = 0;
        _tail = 0;
        _readerCount = 0;
        _writerCount = 0;
        _readersClosed = true;
        _writersClosed = true;
        _readHandle.Reset();
        _writeHandle.Reset();
        ReleasePipeAccount();
        ReturnHandlesToPool();
        if (_namedOwner == null)
            AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "PipeInode.CloseLifecycleOnce");
        else
        {
            if (ReferenceEquals(_namedOwner.NamedPipe, this)) _namedOwner.NamedPipe = null;
            if (_openQueue != null)
            {
                _waitQueues.Scheduler.ReturnAsyncWaitQueue(_openQueue);
                _openQueue = null;
            }
        }

        ReturnBufferToPool();
    }

    private void ReturnBufferToPool()
    {
        if (_buffer.Length == 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
        _buffer = Array.Empty<byte>();
    }

    private void ReleasePipeAccount()
    {
        if (_pipeAccountReleased) return;
        _pipeAccountReleased = true;
        if (_pipeAccountUid >= 0 && _pipeAccountPages > 0)
            _waitQueues.Scheduler.ReleasePipePages(_pipeAccountUid, _pipeAccountPages);
        _pipeAccountPages = 0;
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
