using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fiberish.Core;

namespace Fiberish.VFS;

public class EpollItem
{
    public int Fd;
    public LinuxFile File = null!;
    public uint Events;
    public ulong Data;
    public bool IsReady;
    public IDisposable? WaitRegistration;
}

public class EpollInode : TmpfsInode
{
    private readonly Dictionary<LinuxFile, EpollItem> _watches = new();
    private readonly List<EpollItem> _readyList = new();
    private readonly AsyncWaitQueue _waitQueue = new();
    private readonly object _lock = new();

    public EpollInode(ulong ino, SuperBlock sb) : base(ino, sb)
    {
        Type = InodeType.Fifo;
        Mode = 0x1A4; // pseudo device mode, like pipe
    }

    public int Ctl(int op, int fd, LinuxFile targetFile, uint events, ulong data)
    {
        lock (_lock)
        {
            if (op == Fiberish.Native.LinuxConstants.EPOLL_CTL_ADD)
            {
                if (_watches.ContainsKey(targetFile)) return -(int)Fiberish.Native.Errno.EEXIST;

                var item = new EpollItem { Fd = fd, File = targetFile, Events = events, Data = data };
                _watches[targetFile] = item;

                // Add initial poll and callback registration
                CheckAndRegisterLocked(item);
            }
            else if (op == Fiberish.Native.LinuxConstants.EPOLL_CTL_DEL)
            {
                if (!_watches.ContainsKey(targetFile)) return -(int)Fiberish.Native.Errno.ENOENT;
                if (_watches.TryGetValue(targetFile, out var oldItem))
                    oldItem.WaitRegistration?.Dispose();
                _watches.Remove(targetFile);
                _readyList.RemoveAll(x => x.File == targetFile);
            }
            else if (op == Fiberish.Native.LinuxConstants.EPOLL_CTL_MOD)
            {
                if (!_watches.TryGetValue(targetFile, out var item)) return -(int)Fiberish.Native.Errno.ENOENT;
                item.Events = events;
                item.Data = data;
                item.WaitRegistration?.Dispose();
                item.WaitRegistration = null;

                // For MOD, we re-evaluate readiness
                if (item.IsReady)
                {
                    _readyList.Remove(item);
                    item.IsReady = false;
                }

                CheckAndRegisterLocked(item);
            }
            else
            {
                return -(int)Fiberish.Native.Errno.EINVAL;
            }

            return 0;
        }
    }

    private void CheckAndRegisterLocked(EpollItem item)
    {
        item.WaitRegistration?.Dispose();
        item.WaitRegistration = null;

        // For polling, we map EPOLL events to POLL events expected by the underlying Inode.Poll
        // EPOLLIN == POLLIN, EPOLLOUT == POLLOUT, etc.
        short watchEvents = (short)(item.Events & 0xFFFF);

        short currentEvents = item.File.Dentry.Inode!.Poll(item.File, watchEvents);

        // Always include EPOLLERR and EPOLLHUP even if not explicitly requested
        short mask = (short)(watchEvents | Fiberish.Native.LinuxConstants.EPOLLERR |
                             Fiberish.Native.LinuxConstants.EPOLLHUP);

        if ((currentEvents & mask) != 0)
        {
            if (!item.IsReady)
            {
                item.IsReady = true;
                _readyList.Add(item);
                _waitQueue.Signal(); // Wake up an awaiting FiberTask (epoll_wait)
            }

            // Item is already ready - no need to register a wait callback.
            // When HarvestEventsLocked processes this item, for LT it will call
            // CheckAndRegisterLocked again to re-arm if data still available.
            return;
        }

        // Item is NOT ready. Register a callback on the target Inode so we get notified of future changes.
        // We only do this when not ready to avoid firing the callback synchronously
        // while holding _lock (which would deadlock since the callback also acquires _lock).
        item.WaitRegistration = item.File.Dentry.Inode.RegisterWaitHandle(item.File, () =>
        {
            lock (_lock)
            {
                if (_watches.ContainsValue(item))
                {
                    // Re-evaluate on wakeup
                    CheckAndRegisterLocked(item);
                }
            }
        }, watchEvents);
    }


    public int TryHarvestNow(byte[] eventsBuffer, int maxEvents)
    {
        lock (_lock)
        {
            if (_readyList.Count > 0)
                return HarvestEventsLocked(eventsBuffer, maxEvents);
            return 0;
        }
    }

    public EpollAwaitable WaitAsync(byte[] eventsBuffer, int maxEvents, int timeout)
    {
        return new EpollAwaitable(this, eventsBuffer, maxEvents, timeout);
    }

    public readonly struct EpollAwaitable
    {
        private readonly EpollAwaitState _state;

        public EpollAwaitable(EpollInode inode, byte[] buffer, int maxEvents, int timeoutMs)
        {
            _state = new EpollAwaitState(inode, buffer, maxEvents, timeoutMs);
        }

        public EpollAwaiter GetAwaiter() => new(_state);
    }

    public readonly struct EpollAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        private readonly EpollAwaitState _state;

        internal EpollAwaiter(EpollAwaitState state)
        {
            _state = state;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            _state.OnCompleted(continuation);
        }

        public int GetResult() => _state.GetResult();
    }

    internal sealed class EpollAwaitState
    {
        private readonly EpollInode _inode;
        private readonly byte[] _buffer;
        private readonly int _maxEvents;
        private readonly int _timeoutMs;
        private bool _completed;
        private Action? _continuation;
        private bool _hasTimedOut;
        private int _reschedulePending;
        private int _result;
        private FiberTask _task = null!;
        private FiberTask.WaitToken? _token;
        private Fiberish.Core.Timer? _timer;
        private IDisposable? _queueWaitRegistration;

        public EpollAwaitState(EpollInode inode, byte[] buffer, int maxEvents, int timeoutMs)
        {
            _inode = inode;
            _buffer = buffer;
            _maxEvents = maxEvents;
            _timeoutMs = timeoutMs;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current!;
            _task = scheduler.CurrentTask!;
            _continuation = continuation;
            _token = _task.BeginWaitToken();

            if (_timeoutMs > 0)
            {
                _timer = scheduler.ScheduleTimer(_timeoutMs, () =>
                {
                    _hasTimedOut = true;
                    ScheduleRePoll();
                });
            }

            DoPoll();

            if (!_completed)
                _task.ArmSignalSafetyNet(_token, () => ScheduleRePoll());
        }

        public int GetResult()
        {
            _queueWaitRegistration?.Dispose();
            _queueWaitRegistration = null;
            if (_token != null) _task.CompleteWaitToken(_token);
            return _result;
        }

        private void ScheduleRePoll()
        {
            if (System.Threading.Interlocked.Exchange(ref _reschedulePending, 1) == 0)
            {
                KernelScheduler.Current!.Schedule(() =>
                {
                    _reschedulePending = 0;
                    DoPoll();
                });
            }
        }

        private void DoPoll()
        {
            if (_completed) return;
            _queueWaitRegistration?.Dispose();
            _queueWaitRegistration = null;

            if (_task.HasUnblockedPendingSignal())
            {
                _timer?.Cancel();
                _result = -(int)Fiberish.Native.Errno.ERESTARTSYS;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            int ready;
            lock (_inode._lock)
            {
                ready = _inode._readyList.Count > 0
                    ? _inode.HarvestEventsLocked(_buffer, _maxEvents)
                    : 0;
            }

            if (ready > 0)
            {
                _timer?.Cancel();
                _result = ready;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            if (_hasTimedOut || _timeoutMs == 0)
            {
                _result = 0;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            _queueWaitRegistration = _inode._waitQueue.RegisterCancelable(ScheduleRePoll, _task, _token);
        }
    }

    private int HarvestEventsLocked(byte[] buffer, int maxEvents)
    {
        int count = Math.Min(_readyList.Count, maxEvents);
        int structSize = 12; // Linux i386 struct epoll_event is 12 bytes packed (__uint32_t events, __uint64_t data)

        // Items to re-evaluate for Level-Triggered
        var reEvalList = new List<EpollItem>();

        int harvested = 0;
        for (int i = 0; i < count; i++)
        {
            var item = _readyList[i];

            // Re-poll the underlying FD one last time exactly at harvest to get the precise mask
            short watchEvents = (short)(item.Events & 0xFFFF);
            short currentEvents = item.File.Dentry.Inode!.Poll(item.File, watchEvents);
            short mask = (short)(watchEvents | Fiberish.Native.LinuxConstants.EPOLLERR |
                                 Fiberish.Native.LinuxConstants.EPOLLHUP);

            uint finalEvents = (uint)(currentEvents & mask);

            if (finalEvents == 0)
            {
                // False alarm or event cleared before we harvested
                item.IsReady = false;
                continue;
            }

            // Write into the byte span
            Span<byte> evSpan = buffer.AsSpan().Slice(harvested * structSize, structSize);
            BinaryPrimitives.WriteUInt32LittleEndian(evSpan.Slice(0, 4), finalEvents);
            BinaryPrimitives.WriteUInt64LittleEndian(evSpan.Slice(4, 8), item.Data);

            item.IsReady = false;
            harvested++;

            if ((item.Events & Fiberish.Native.LinuxConstants.EPOLLONESHOT) != 0)
            {
                // Oneshot means it's disabled after triggering
                item.Events &= ~(uint)0xFFFF; // Clear watch events, keep data
            }
            else if ((item.Events & Fiberish.Native.LinuxConstants.EPOLLET) == 0)
            {
                // Level-triggered: it should keep firing if data is still available.
                // We'll re-evaluate it immediately after slicing the read queue.
                reEvalList.Add(item);
            }
        }

        _readyList.RemoveRange(0, count);

        // Re-arm LT events implicitly by re-checking them
        foreach (var item in reEvalList)
        {
            if (_watches.ContainsValue(item))
            {
                CheckAndRegisterLocked(item);
            }
        }

        return harvested;
    }
}
