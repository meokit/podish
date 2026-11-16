using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.VFS;

internal sealed class EpollItem
{
    public ulong Data;
    public IReadyDispatcher? Dispatcher;
    public bool IsDeferredEdgeRearmQueued;
    public bool IsEdgeArmed;
    public uint Events;
    public int Fd;
    public LinuxFile File = null!;
    public bool IsReady;
    public short LastObservedEvents;
    public short Mask;
    public bool NeedsEdgeRearm;
    public IDisposable? WaitRegistration;
    public Action? TargetClosing;
    public bool PersistentNotifications;
    public bool OneShotDisarmed;
    public readonly Lock LifetimeGate = new();
    public int TargetClosed;
    public IDisposable? NotificationRegistration;
}

public class EpollInode : TmpfsInode, IDispatcherWaitSource, IDispatcherEdgeWaitSource
{
    private const int EpollEventSize = 12;
    private const int MaxEpollNestingEdges = 4;
    private readonly Queue<EpollItem> _deferredEdgeRearms = new();
    private readonly HashSet<EpollInode> _parents = new();
    private readonly List<EpollItem> _readyList = new();
    private readonly KernelScheduler _scheduler;
    private readonly AsyncWaitQueue _waitQueue;
    private readonly Dictionary<LinuxFile, EpollItem> _watches = new();
    private readonly LinkedList<EpollAwaitState> _blockedWaiters = new();
    private readonly Lock _lifetimeGate = new();
    private int _released;

    public EpollInode(ulong ino, SuperBlock sb, KernelScheduler scheduler) : base(ino, sb)
    {
        _scheduler = scheduler;
        _waitQueue = new AsyncWaitQueue(scheduler);
        Type = InodeType.Fifo;
        Mode = 0x1A4; // pseudo device mode, like pipe
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        if (Volatile.Read(ref _released) != 0) return 0;
        const short POLLIN = 0x0001;

        if ((events & POLLIN) == 0)
            return 0;

        RefreshDeferredEdgeRearms();
        return _readyList.Count > 0 ? POLLIN : (short)0;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return false;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.Register(callback, _scheduler, events, readWatch);
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return null;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.RegisterHandle(callback, _scheduler, events, readWatch);
    }

    public override IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return null;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, _scheduler, events, readWatch);
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return false;

        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return false;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.Register(callback, scheduler, events, readWatch);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return null;

        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch);
    }

    IDisposable? IDispatcherEdgeWaitSource.RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) == 0)
            return null;

        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;

        var readWatch = new QueueReadinessWatch(POLLIN, () => IsReadable(), _waitQueue, _waitQueue.Reset);
        return QueueReadinessRegistration.RegisterHandleOnNextSignal(callback, scheduler, events, readWatch);
    }

    public int Ctl(FiberTask task, int op, int fd, LinuxFile targetFile, uint events, ulong data)
    {
        AssertSchedulerThread();
        if (Volatile.Read(ref _released) != 0 || targetFile.IsClosed) return -(int)Errno.EBADF;
        var dispatcher = new SchedulerReadyDispatcher(_scheduler);
        if (op == LinuxConstants.EPOLL_CTL_ADD)
        {
            if (_watches.ContainsKey(targetFile)) return -(int)Errno.EEXIST;

            if (targetFile.OpenedInode is EpollInode childEpoll)
            {
                if (ReferenceEquals(childEpoll, this) || childEpoll.CanReach(this))
                    return -(int)Errno.ELOOP;
                if (GetMaxAncestorEdges() + 1 + childEpoll.GetMaxDescendantEdges() > MaxEpollNestingEdges)
                    return -(int)Errno.EINVAL;
            }

            var item = new EpollItem
                { Fd = fd, File = targetFile, Events = events, Data = data, Dispatcher = dispatcher };
            lock (_lifetimeGate)
            {
                if (_released != 0) return -(int)Errno.EBADF;
                _watches[targetFile] = item;
                item.TargetClosing = () => OnTargetClosing(item);
                targetFile.FinalClosing += item.TargetClosing;
            }
            if (targetFile.OpenedInode is EpollInode child) child._parents.Add(this);

            // Add initial poll and callback registration
            CheckAndRegister(item);
        }
        else if (op == LinuxConstants.EPOLL_CTL_DEL)
        {
            if (!_watches.TryGetValue(targetFile, out var oldItem)) return -(int)Errno.ENOENT;
            RemoveWatch(oldItem);
        }
        else if (op == LinuxConstants.EPOLL_CTL_MOD)
        {
            if (!_watches.TryGetValue(targetFile, out var item)) return -(int)Errno.ENOENT;
            if ((item.Events & LinuxConstants.EPOLLEXCLUSIVE) != 0) return -(int)Errno.EINVAL;
            item.Events = events;
            item.OneShotDisarmed = false;
            item.Data = data;
            item.Dispatcher = dispatcher;
            item.NeedsEdgeRearm = false;
            item.IsEdgeArmed = false;
            item.WaitRegistration?.Dispose();
            item.WaitRegistration = null;
            Interlocked.Exchange(ref item.NotificationRegistration, null)?.Dispose();

            // For MOD, we re-evaluate readiness
            if (item.IsReady)
            {
                _readyList.Remove(item);
                item.IsReady = false;
                if (_readyList.Count == 0)
                    _waitQueue.Reset();
            }

            CheckAndRegister(item);
        }
        else
        {
            return -(int)Errno.EINVAL;
        }

        return 0;
    }

    private void CheckAndRegister(EpollItem item)
    {
        AssertSchedulerThread();
        lock (item.LifetimeGate) CheckAndRegisterAlive(item);
    }

    private void CheckAndRegisterAlive(EpollItem item)
    {
        if (!IsWatchActive(item)) return;
        if (item.File.IsClosed)
        {
            OnTargetClosing(item);
            return;
        }
        var openedInode = item.File.IoInode
            ?? throw new InvalidOperationException("Epoll watch target inode is unavailable.");
        if (openedInode is IPollNotificationSource source)
        {
            item.PersistentNotifications = true;
            item.Mask = (short)(item.Events | LinuxConstants.EPOLLERR | LinuxConstants.EPOLLHUP);
            if (item.NotificationRegistration == null)
            {
                var registration = source.SubscribePoll(item.File, item.Dispatcher!,
                    _ => !item.OneShotDisarmed && NotifyReady(item),
                    item.Mask, (item.Events & LinuxConstants.EPOLLEXCLUSIVE) != 0);
                Interlocked.Exchange(ref item.NotificationRegistration, registration)?.Dispose();
                if (!IsWatchActive(item))
                    Interlocked.Exchange(ref item.NotificationRegistration, null)?.Dispose();
            }
            if ((PollWatch(item, item.Mask) & item.Mask) != 0) NotifyReady(item);
            return;
        }
        item.WaitRegistration?.Dispose();
        item.WaitRegistration = null;

        // For polling, we map EPOLL events to POLL events expected by the underlying Inode.Poll
        // EPOLLIN == POLLIN, EPOLLOUT == POLLOUT, etc.
        var watchEvents = (short)(item.Events & 0xFFFF);

        var currentEvents = PollWatch(item, watchEvents);

        // Always include EPOLLERR and EPOLLHUP even if not explicitly requested
        var mask = (short)(watchEvents | LinuxConstants.EPOLLERR |
                           LinuxConstants.EPOLLHUP);
        item.Mask = mask;

        if (item.NeedsEdgeRearm)
        {
            if ((currentEvents & mask) != 0)
                return;

            item.NeedsEdgeRearm = false;
        }

        var isEdge = (item.Events & LinuxConstants.EPOLLET) != 0;
        var effective = (short)(currentEvents & mask);
        item.LastObservedEvents = effective;
        if (effective == 0)
            goto registerWait;
        _ = isEdge;

        if (!item.IsReady)
        {
            NotifyReady(item);
        }

        // Item is already ready - no need to register a wait callback.
        // When HarvestEvents processes this item, for LT it will call CheckAndRegister again.
        return;

    registerWait:
        // Callback dispatch-to-scheduler is owned by readiness providers (e.g. HostSocketProbeEngine).
        // EpollInode itself is strict scheduler-thread-only.
        void RePoll()
        {
            AssertSchedulerThread();
            if (IsWatchActive(item))
                CheckAndRegister(item);
        }

        if ((item.Events & LinuxConstants.EPOLLET) != 0 &&
            openedInode is IDispatcherEdgeWaitSource edgeWaitSource)
            item.WaitRegistration = edgeWaitSource.RegisterEdgeTriggeredWaitHandle(item.File,
                item.Dispatcher ?? throw new InvalidOperationException("Epoll host socket watch requires dispatcher."),
                RePoll, watchEvents);
        else if (openedInode is IDispatcherWaitSource dispatcherWaitSource)
            item.WaitRegistration = dispatcherWaitSource.RegisterWaitHandle(item.File,
                item.Dispatcher ?? throw new InvalidOperationException("Epoll host socket watch requires dispatcher."),
                RePoll, watchEvents);
        else
            item.WaitRegistration = openedInode.RegisterWaitHandle(item.File, RePoll, watchEvents);
    }

    private void RefreshDeferredEdgeRearms()
    {
        AssertSchedulerThread();
        var pendingCount = _deferredEdgeRearms.Count;
        for (var i = 0; i < pendingCount; i++)
        {
            var item = _deferredEdgeRearms.Dequeue();
            item.IsDeferredEdgeRearmQueued = false;

            if (!item.NeedsEdgeRearm || item.IsReady || item.WaitRegistration != null)
                continue;

            if (!IsWatchActive(item))
                continue;

            if ((item.Events & 0xFFFF) == 0)
            {
                item.NeedsEdgeRearm = false;
                continue;
            }

            CheckAndRegister(item);
            if (item.NeedsEdgeRearm)
                EnqueueDeferredEdgeRearm(item);
        }
    }

    private void RearmEdgeTriggeredItem(EpollItem item)
    {
        AssertSchedulerThread();
        lock (item.LifetimeGate) RearmLiveEdgeTriggeredItem(item);
    }

    private void RearmLiveEdgeTriggeredItem(EpollItem item)
    {
        if (!IsWatchActive(item)) return;
        item.WaitRegistration?.Dispose();
        item.WaitRegistration = null;
        item.NeedsEdgeRearm = false;

        var watchEvents = (short)(item.Events & 0xFFFF);
        if (watchEvents == 0)
            return;

        var openedInode = item.File.IoInode
                          ?? throw new InvalidOperationException("Epoll watch target inode is unavailable.");

        if (openedInode is IDispatcherEdgeWaitSource edgeWaitSource)
        {
            var dispatcher = item.Dispatcher
                             ?? throw new InvalidOperationException(
                                 "Epoll edge-triggered watch requires dispatcher.");
            item.WaitRegistration = edgeWaitSource.RegisterEdgeTriggeredWaitHandle(item.File,
                dispatcher,
                RePollForEdge, watchEvents);
            if (item.WaitRegistration != null)
            {
                item.IsEdgeArmed = true;
                return;
            }
        }
        else
        {
            item.WaitRegistration = openedInode.RegisterEdgeTriggeredWaitHandle(item.File, RePollForEdge, watchEvents);
            if (item.WaitRegistration != null)
            {
                item.IsEdgeArmed = true;
                return;
            }
        }

        item.NeedsEdgeRearm = true;
        EnqueueDeferredEdgeRearm(item);
        return;

        void RePollForEdge()
        {
            AssertSchedulerThread();
            if (!IsWatchActive(item))
                return;

            item.IsEdgeArmed = false;
            CheckAndRegister(item);
        }
    }

    private bool CanReach(EpollInode target)
    {
        foreach (var item in _watches.Values)
        {
            if (item.File.OpenedInode is EpollInode child)
            {
                if (ReferenceEquals(child, target) || child.CanReach(target))
                    return true;
            }
        }

        return false;
    }

    private int GetMaxDescendantEdges()
    {
        var maxChild = 0;
        foreach (var item in _watches.Values)
        {
            if (item.File.OpenedInode is EpollInode child)
                maxChild = Math.Max(maxChild, 1 + child.GetMaxDescendantEdges());
        }

        return maxChild;
    }

    private int GetMaxAncestorEdges()
    {
        var maxParent = 0;
        foreach (var parent in _parents)
            maxParent = Math.Max(maxParent, 1 + parent.GetMaxAncestorEdges());
        return maxParent;
    }

    public override void Release(LinuxFile linuxFile)
    {
        EpollItem[] watches;
        lock (_lifetimeGate)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            watches = _watches.Values.ToArray();
        }
        // Cancel source leases now, even when the owner scheduler has stopped. Mutable epoll
        // lists and scheduler-bound legacy registrations are cleaned only on the owner.
        foreach (var item in watches) RetireWatch(item);
        RunOnOwner(() =>
        {
            foreach (var item in watches) RemoveWatch(item);
            _readyList.Clear();
            _deferredEdgeRearms.Clear();
        });
        base.Release(linuxFile);
    }

    private void RunOnOwner(Action action)
    {
        if (_scheduler.IsSchedulerThread) action();
        else _scheduler.ScheduleFromAnyThread(action);
    }

    private static void RetireWatch(EpollItem item)
    {
        // FinalClosing waits for an in-flight Poll before allowing Inode.Release/PrivateData
        // teardown. It does NOT wait for the owner scheduler to process its ingress queue.
        lock (item.LifetimeGate) Volatile.Write(ref item.TargetClosed, 1);
        Interlocked.Exchange(ref item.NotificationRegistration, null)?.Dispose();
    }

    private void OnTargetClosing(EpollItem item)
    {
        RetireWatch(item);
        RunOnOwner(() => RemoveWatch(item));
    }

    private void RemoveWatch(EpollItem item)
    {
        if (!IsWatchRegistered(item)) return;
        lock (_lifetimeGate) _watches.Remove(item.File);
        item.File.FinalClosing -= item.TargetClosing;
        item.TargetClosing = null;
        item.WaitRegistration?.Dispose();
        item.WaitRegistration = null;
        Interlocked.Exchange(ref item.NotificationRegistration, null)?.Dispose();
        item.NeedsEdgeRearm = item.IsEdgeArmed = false;
        _readyList.Remove(item);
        item.IsReady = false;
        if (item.File.OpenedInode is EpollInode child) child._parents.Remove(this);
        if (_readyList.Count == 0) _waitQueue.Reset();
    }

    private bool NotifyReady(EpollItem item)
    {
        if (!IsWatchActive(item)) return false;
        if (!item.IsReady)
        {
            item.IsReady = true;
            _readyList.Add(item);
        }
        _waitQueue.Signal(); // poll/select/nested-epoll listeners, not the epoll_wait threads.
        return WakeBlockedWaiter();
    }

    private bool WakeBlockedWaiter()
    {
        while (_blockedWaiters.First is { } node)
        {
            node.Value.UnregisterWaiter();
            if (node.Value.TryWakeForReady()) return true;
        }
        return false;
    }

    private bool IsWatchRegistered(EpollItem item)
    {
        return _watches.TryGetValue(item.File, out var current) && ReferenceEquals(current, item);
    }

    private bool IsWatchActive(EpollItem item) => Volatile.Read(ref _released) == 0 &&
        Volatile.Read(ref item.TargetClosed) == 0 && IsWatchRegistered(item);

    private short PollWatch(EpollItem item, short events)
    {
        lock (item.LifetimeGate)
            return IsWatchActive(item) ? item.File.IoInode!.Poll(item.File, events) : (short)0;
    }

    private void RefreshPendingWatches()
    {
        foreach (var item in _watches.Values)
        {
            if (!IsWatchActive(item)) continue;
            if (item.PersistentNotifications) continue;
            var watchEvents = (short)(item.Events & 0xFFFF);
            if (watchEvents == 0) continue;

            if ((item.Events & LinuxConstants.EPOLLET) != 0)
            {
                // Edge-triggered: the async re-poll may not have run yet when epoll_wait is
                // entered, so sample the state here too and report a rising edge exactly once.
                if (item.IsReady || item.NeedsEdgeRearm) continue;
                var inode = item.File.OpenedInode;
                if (inode == null) continue;
                var current = (short)(PollWatch(item, watchEvents) & item.Mask);
                var rising = (short)(current & ~item.LastObservedEvents);
                item.LastObservedEvents = current;
                if (rising == 0) continue;

                item.WaitRegistration?.Dispose();
                item.WaitRegistration = null;
                item.IsEdgeArmed = false;
                item.NeedsEdgeRearm = false;
            }
            else
            {
                if (item.IsReady || item.NeedsEdgeRearm || item.IsEdgeArmed) continue;
                var inode = item.File.OpenedInode;
                if (inode == null) continue;
                var current = (short)(PollWatch(item, watchEvents) & item.Mask);
                item.LastObservedEvents = current;
                if (current == 0) continue;
            }

            NotifyReady(item);
        }
    }

    private void EnqueueDeferredEdgeRearm(EpollItem item)
    {
        if (item.IsDeferredEdgeRearmQueued)
            return;

        item.IsDeferredEdgeRearmQueued = true;
        _deferredEdgeRearms.Enqueue(item);
    }

    private bool IsReadable()
    {
        AssertSchedulerThread();
        RefreshDeferredEdgeRearms();
        RefreshPendingWatches();
        return _readyList.Count > 0;
    }


    public int TryHarvestNow(Span<byte> eventsBuffer, int maxEvents)
    {
        if (Volatile.Read(ref _released) != 0) return 0;
        AssertSchedulerThread();
        RefreshDeferredEdgeRearms();
        RefreshPendingWatches();
        if (_readyList.Count > 0)
            return HarvestEvents(eventsBuffer, maxEvents);
        return 0;
    }

    public EpollAwaitable WaitAsync(FiberTask task, byte[] eventsBuffer, int maxEvents, int timeout,
        uint syscallNr = 0)
    {
        return new EpollAwaitable(task, this, eventsBuffer, maxEvents, timeout, syscallNr);
    }

    private int HarvestEvents(Span<byte> buffer, int maxEvents)
    {
        AssertSchedulerThread();
        var capacity = _readyList.Count;
        EpollItem[]? reEvalItems = null;
        var reEvalCount = 0;
        EpollItem[]? edgeRearmItems = null;
        var edgeRearmCount = 0;

        var harvested = 0;
        var consumed = 0;
        try
        {
            for (var i = 0; i < capacity && harvested < maxEvents; i++)
            {
                var item = _readyList[i];
                consumed = i + 1;

                // Re-poll the underlying FD one last time exactly at harvest to get the precise mask
                var watchEvents = (short)(item.Events & 0xFFFF);
                var currentEvents = PollWatch(item, watchEvents);
                var mask = (short)(watchEvents | LinuxConstants.EPOLLERR |
                                   LinuxConstants.EPOLLHUP);

                var finalEvents = (uint)(currentEvents & mask);
                item.Mask = mask;
                if ((item.Events & LinuxConstants.EPOLLET) != 0)
                    item.LastObservedEvents = (short)(currentEvents & mask);

                if (finalEvents == 0)
                {
                    // False alarm or event cleared before we harvested. It must not consume
                    // one of the caller's maxevents slots.
                    item.IsReady = false;
                    if (!item.PersistentNotifications && (item.Events & 0xFFFF) != 0)
                        AddScratchItem(ref reEvalItems, ref reEvalCount, capacity, item);
                    continue;
                }

                var evSpan = buffer.Slice(harvested * EpollEventSize, EpollEventSize);
                BinaryPrimitives.WriteUInt32LittleEndian(evSpan.Slice(0, 4), finalEvents);
                BinaryPrimitives.WriteUInt64LittleEndian(evSpan.Slice(4, 8), item.Data);

                item.IsReady = false;
                harvested++;

                if ((item.Events & LinuxConstants.EPOLLONESHOT) != 0)
                {
                    // Oneshot means it's disabled after triggering
                    item.Events &= ~(uint)0xFFFF; // Clear watch events, keep data
                    item.OneShotDisarmed = true;
                }
                else if (item.PersistentNotifications)
                {
                    if ((item.Events & LinuxConstants.EPOLLET) == 0)
                        AddScratchItem(ref reEvalItems, ref reEvalCount, capacity, item);
                }
                else if ((item.Events & LinuxConstants.EPOLLET) != 0)
                    AddScratchItem(ref edgeRearmItems, ref edgeRearmCount, capacity, item);
                else if ((item.Events & LinuxConstants.EPOLLET) == 0)
                    // Level-triggered: it should keep firing if data is still available.
                    // We'll re-evaluate it immediately after slicing the read queue.
                    AddScratchItem(ref reEvalItems, ref reEvalCount, capacity, item);
            }

            if (consumed > 0)
                _readyList.RemoveRange(0, consumed);

            // Re-arm LT events implicitly by re-checking them
            for (var i = 0; i < reEvalCount; i++)
            {
                var item = reEvalItems![i];
                if (IsWatchActive(item))
                {
                    if (item.PersistentNotifications)
                    {
                        // Keep LT as a candidate for the NEXT harvest. Poll may consume a procfs
                        // event, so neither notifications nor this requeue may call Poll again.
                        item.IsReady = true;
                        _readyList.Add(item);
                    }
                    else CheckAndRegister(item);
                }
            }

            for (var i = 0; i < edgeRearmCount; i++)
            {
                var item = edgeRearmItems![i];
                if (IsWatchActive(item))
                    RearmEdgeTriggeredItem(item);
            }

            if (_readyList.Count == 0)
                _waitQueue.Reset();
            else
            {
                // A successful LT delivery (or maxevents leaving queued candidates) must
                // hand readiness to the next blocked thread even without another source edge.
                // Do not Poll here: a queued procfs candidate may consume its event on recheck.
                _waitQueue.Signal();
                WakeBlockedWaiter();
            }

            return harvested;
        }
        finally
        {
            if (reEvalItems != null)
            {
                Array.Clear(reEvalItems, 0, reEvalCount);
                ArrayPool<EpollItem>.Shared.Return(reEvalItems, clearArray: false);
            }
            if (edgeRearmItems != null)
            {
                Array.Clear(edgeRearmItems, 0, edgeRearmCount);
                ArrayPool<EpollItem>.Shared.Return(edgeRearmItems, clearArray: false);
            }
        }

        static void AddScratchItem(ref EpollItem[]? items, ref int itemCount, int capacity, EpollItem item)
        {
            items ??= ArrayPool<EpollItem>.Shared.Rent(capacity);
            items[itemCount++] = item;
        }
    }

    private static void AssertSchedulerThread([CallerMemberName] string? caller = null)
    {
        // TODO: inject KernelScheduler
    }

    public readonly struct EpollAwaitable
    {
        private readonly EpollAwaitState _state;

        public EpollAwaitable(FiberTask task, EpollInode inode, byte[] buffer, int maxEvents, int timeoutMs,
            uint syscallNr)
        {
            _state = new EpollAwaitState(task, inode, buffer, maxEvents, timeoutMs, syscallNr);
        }

        public EpollAwaiter GetAwaiter()
        {
            return new EpollAwaiter(_state);
        }
    }

    public readonly struct EpollAwaiter : INotifyCompletion
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

        public int GetResult()
        {
            return _state.GetResult();
        }
    }

    internal sealed class EpollAwaitState
    {
        private readonly byte[] _buffer;
        private readonly EpollInode _inode;
        private readonly int _maxEvents;
        private readonly KernelScheduler _scheduler = null!;
        private readonly FiberTask _task = null!;
        private readonly int _timeoutMs;
        private bool _completed;
        private Action? _continuation;
        private bool _hasTimedOut;
        private TaskAsyncOperationHandle? _operation;
        private IDisposable? _queueWaitRegistration;
        private LinkedListNode<EpollAwaitState>? _waiterNode;
        private int _reschedulePending;
        private int _result;
        private Timer? _timer;
        private FiberTask.WaitToken? _token;

        public EpollAwaitState(FiberTask task, EpollInode inode, byte[] buffer, int maxEvents, int timeoutMs,
            uint syscallNr)
        {
            _task = task;
            _scheduler = task.CommonKernel;
            _inode = inode;
            _buffer = buffer;
            _maxEvents = maxEvents;
            _timeoutMs = timeoutMs;
        }

        public void OnCompleted(Action continuation)
        {
            _continuation = continuation;
            _token = _task.BeginWaitToken();
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
                return;
            _operation = operation;
            _operation.TryInitialize(continuation);

            if (_timeoutMs > 0)
            {
                _timer = _scheduler.ScheduleTimer(_timeoutMs, OnTimeout);
                _operation.TryAddRegistration(TaskAsyncRegistration.From(_timer));
            }

            DoPoll();

            if (!_completed)
                _task.ArmInterruptingSignalSafetyNet(_token, OnSignal);
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
            if (Interlocked.Exchange(ref _reschedulePending, 1) == 0)
                _scheduler.ScheduleContinuation(OnRePollScheduled, _task);
        }

        internal bool TryWakeForReady()
        {
            if (_completed || _hasTimedOut || _operation is not { IsActive: true } ||
                _task.IsRetiring || _task.HasInterruptingPendingSignal() ||
                Interlocked.Exchange(ref _reschedulePending, 1) != 0) return false;
            // Do not consume the operation's wait token until a final result is available:
            // a stale-ready recheck must be able to block and wake again with the same operation.
            _scheduler.NoteInteractiveWake(_task);
            _scheduler.ScheduleContinuation(OnRePollScheduled, _task);
            return true;
        }

        internal void UnregisterWaiter()
        {
            if (_waiterNode == null) return;
            _inode._blockedWaiters.Remove(_waiterNode);
            _waiterNode = null;
        }

        private sealed class WaiterRegistration(EpollAwaitState state) : IDisposable
        {
            public void Dispose() => state.UnregisterWaiter();
        }

        private void OnTimeout()
        {
            _hasTimedOut = true;
            ScheduleRePoll();
        }

        private void OnSignal()
        {
            _result = -(int)Errno.ERESTARTSYS;
            _completed = true;
            _operation?.TryComplete(WakeReason.Signal);
        }

        private void OnRePollScheduled()
        {
            _reschedulePending = 0;
            DoPoll();
        }

        private void DoPoll()
        {
            _scheduler.AssertSchedulerThread();
            if (_completed || _operation is not { IsActive: true }) return;
            _queueWaitRegistration?.Dispose();
            _queueWaitRegistration = null;

            _inode.RefreshDeferredEdgeRearms();
            _inode.RefreshPendingWatches();

            if (_task.HasInterruptingPendingSignal())
            {
                _timer?.Cancel();
                _result = -(int)Errno.ERESTARTSYS;
                _completed = true;
                _operation?.TryComplete(WakeReason.Signal);
                return;
            }

            var ready = _inode._readyList.Count > 0
                ? _inode.HarvestEvents(_buffer, _maxEvents)
                : 0;

            if (ready > 0)
            {
                _timer?.Cancel();
                _result = ready;
                _completed = true;
                _operation?.TryComplete(WakeReason.Event);
                return;
            }

            if (_hasTimedOut || _timeoutMs == 0)
            {
                _result = 0;
                _completed = true;
                _operation?.TryComplete(WakeReason.Timer);
                return;
            }

            _waiterNode = _inode._blockedWaiters.AddLast(this);
            _queueWaitRegistration = new WaiterRegistration(this);
            _operation?.TryAddRegistration(TaskAsyncRegistration.From(_queueWaitRegistration));
        }
    }
}
