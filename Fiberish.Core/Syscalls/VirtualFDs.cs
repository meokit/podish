using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Timer = Fiberish.Core.Timer;
using WaitHandle = Fiberish.Core.AsyncWaitQueue;

namespace Fiberish.Syscalls;

public class EventFdInode : TmpfsInode, ITaskWaitSource, IDispatcherWaitSource
{
    private const ulong MaxCounter = ulong.MaxValue - 1;
    private readonly bool _isSemaphore;
    private ulong _counter;
    private bool _lifecycleClosed;
    private WaitHandle? _readHandle;
    private PooledReadWriteWaitQueues? _waitQueues;
    private WaitHandle? _writeHandle;

    public EventFdInode(ulong ino, SuperBlock superBlock, KernelScheduler? scheduler, ulong initval, FileFlags flags) :
        base(ino, superBlock)
    {
        if (scheduler != null)
            EnsureWaitQueues(scheduler);
        _counter = initval;
        _isSemaphore = (flags & (FileFlags)LinuxConstants.EFD_SEMAPHORE) != 0;
        AnonymousInodeLifecycle.Initialize(this, "EventFdInode.ctor");
        _writeHandle?.Set();
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        return default;
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Eventfd readiness wait requires an explicit scheduler.");
        EnsureWaitQueues(scheduler);
        return RegisterWaitCore(callback, scheduler, null, events);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException("Eventfd readiness wait requires an explicit scheduler.");
        EnsureWaitQueues(scheduler);
        return RegisterWaitHandleCore(callback, scheduler, null, events);
    }

    bool ITaskWaitSource.RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        EnsureWaitQueues(task.CommonKernel);
        return RegisterWaitCore(callback, null, task, events);
    }

    IDisposable? ITaskWaitSource.RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        EnsureWaitQueues(task.CommonKernel);
        return RegisterWaitHandleCore(callback, null, task, events);
    }

    protected internal override int ReadSpan(LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return 0;

            if (_counter == 0)
                // Linux eventfd(2): reads on a zero counter block unless O_NONBLOCK is set.
                // We surface EAGAIN here so the generic VFS read path can call WaitForRead().
                return -(int)Errno.EAGAIN;

            var wasFull = _counter == MaxCounter;

            ulong val;
            if (_isSemaphore)
            {
                val = 1;
                _counter--;
            }
            else
            {
                val = _counter;
                _counter = 0;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], val);

            if (_counter == 0)
                _readHandle?.Reset();
            if (wasFull)
                _writeHandle?.Set();

            return 8;
        }
    }

    protected internal override int WriteSpan(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        var add = BinaryPrimitives.ReadUInt64LittleEndian(buffer[..8]);
        if (add == 0xFFFFFFFFFFFFFFFF) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return -(int)Errno.EPIPE;

            if (MaxCounter - _counter < add)
                // Linux eventfd(2): writes that would overflow block unless O_NONBLOCK is set.
                // Return EAGAIN so the generic VFS write path can wait for POLLOUT.
                return -(int)Errno.EAGAIN;

            var wasEmpty = _counter == 0;
            _counter += add;
            if (wasEmpty)
                _readHandle?.Set();
            if (_counter == MaxCounter)
                _writeHandle?.Reset();
            return 8;
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return 0;

            if ((events & LinuxConstants.POLLIN) != 0 && _counter > 0)
                revents |= LinuxConstants.POLLIN;
            // Linux eventfd(2): POLLOUT is reported if writing a value of at least 1 would not block.
            if ((events & LinuxConstants.POLLOUT) != 0 && _counter < MaxCounter)
                revents |= LinuxConstants.POLLOUT;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        var registration = RegisterWaitHandle(file, callback, events);
        return registration != null;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            if (((events & LinuxConstants.POLLIN) != 0 && _counter > 0) ||
                ((events & LinuxConstants.POLLOUT) != 0 && _counter < MaxCounter))
            {
                callback();
                return NoopWaitRegistration.Instance;
            }

            if (_waitQueues == null || _readHandle == null || _writeHandle == null)
                return null;

            var readWatch = new QueueReadinessWatch(LinuxConstants.POLLIN, () => _counter > 0, _readHandle,
                _readHandle.Reset);
            var writeWatch = new QueueReadinessWatch(LinuxConstants.POLLOUT, () => _counter < MaxCounter, _writeHandle,
                _writeHandle.Reset);
            return QueueReadinessRegistration.RegisterHandle(callback, _waitQueues.Scheduler, events, readWatch,
                writeWatch);
        }
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task)
    {
        EnsureWaitQueues(task.CommonKernel);
        WaitHandle? readHandle;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return AwaitResult.Completed;

            if (_counter > 0)
                return AwaitResult.Completed;

            readHandle = _readHandle;
            if (readHandle == null)
                return AwaitResult.Completed;

            if (readHandle.IsSignaled)
                readHandle.Reset();
        }

        return await readHandle.WaitInterruptiblyAsync(task);
    }

    public override async ValueTask<AwaitResult> WaitForWrite(LinuxFile file, FiberTask task,
        int minWritableBytes = 1)
    {
        _ = minWritableBytes;
        EnsureWaitQueues(task.CommonKernel);
        WaitHandle? writeHandle;
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return AwaitResult.Completed;

            if (_counter < MaxCounter)
                return AwaitResult.Completed;

            writeHandle = _writeHandle;
            if (writeHandle == null)
                return AwaitResult.Completed;

            if (writeHandle.IsSignaled)
                writeHandle.Reset();
        }

        return await writeHandle.WaitInterruptiblyAsync(task);
    }

    public override void Release(LinuxFile linuxFile)
    {
        CloseLifecycleOnce();
        base.Release(linuxFile);
    }

    protected override void OnEvictCache()
    {
        CloseLifecycleOnce();
        base.OnEvictCache();
    }

    private bool RegisterWaitCore(Action callback, KernelScheduler? scheduler, FiberTask? task, short events)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return false;

            if (_waitQueues == null || _readHandle == null || _writeHandle == null)
                return false;

            var readWatch = new QueueReadinessWatch(LinuxConstants.POLLIN, () => _counter > 0, _readHandle,
                _readHandle.Reset);
            var writeWatch = new QueueReadinessWatch(LinuxConstants.POLLOUT, () => _counter < MaxCounter, _writeHandle,
                _writeHandle.Reset);

            if (task != null)
                return QueueReadinessRegistration.Register(callback, task, events, readWatch, writeWatch);

            return QueueReadinessRegistration.Register(callback, scheduler!, events, readWatch, writeWatch);
        }
    }

    private IDisposable? RegisterWaitHandleCore(Action callback, KernelScheduler? scheduler, FiberTask? task,
        short events)
    {
        using (EnterStateScope())
        {
            if (_lifecycleClosed)
                return null;

            if (_waitQueues == null || _readHandle == null || _writeHandle == null)
                return null;

            var readWatch = new QueueReadinessWatch(LinuxConstants.POLLIN, () => _counter > 0, _readHandle,
                _readHandle.Reset);
            var writeWatch = new QueueReadinessWatch(LinuxConstants.POLLOUT, () => _counter < MaxCounter, _writeHandle,
                _writeHandle.Reset);

            if (task != null)
                return QueueReadinessRegistration.RegisterHandle(callback, task, events, readWatch, writeWatch);

            return QueueReadinessRegistration.RegisterHandle(callback, scheduler!, events, readWatch, writeWatch);
        }
    }

    private void CloseLifecycleOnce()
    {
        if (_lifecycleClosed)
            return;

        _lifecycleClosed = true;
        _counter = 0;
        _readHandle?.Reset();
        _writeHandle?.Reset();
        _waitQueues?.ReturnToPool();
        _readHandle = null;
        _writeHandle = null;
        AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "EventFdInode.CloseLifecycleOnce");
    }

    private void EnsureWaitQueues(KernelScheduler scheduler)
    {
        if (_waitQueues != null)
            return;

        _waitQueues = new PooledReadWriteWaitQueues(scheduler);
        _readHandle = _waitQueues.ReadQueue;
        _writeHandle = _waitQueues.WriteQueue;
        _writeHandle.Set();
        if (_counter > 0)
            _readHandle.Set();
        if (_counter == MaxCounter)
            _writeHandle.Reset();
    }

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public class TimerFdInode : TmpfsInode
{
    private readonly Lock _lock = new();
    private readonly List<Action> _waiters = [];
    private ulong _expirations;

    private long _intervalTicks;
    private KernelScheduler? _scheduler;
    private Timer? _timer;
    private long _valueTicks; // Absolute expiration tick

    public TimerFdInode(ulong ino, SuperBlock superBlock) : base(ino, superBlock)
    {
    }

    public void SetTime(FiberTask task, long intervalTicks, long valueTicks, bool isAbsolute)
    {
        lock (_lock)
        {
            _timer?.Cancel();
            _timer = null;
            var scheduler = task.CommonKernel;
            _scheduler = scheduler;
            _intervalTicks = intervalTicks;

            if (valueTicks > 0)
            {
                _valueTicks = isAbsolute ? valueTicks : scheduler.CurrentTick + valueTicks;

                var delay = Math.Max(0, _valueTicks - scheduler.CurrentTick);
                _timer = scheduler.ScheduleTimer(delay, TimerCallback);
            }
            else
            {
                _valueTicks = 0;
            }
        }
    }

    public void GetTime(FiberTask task, out long intervalTicks, out long valueTicks)
    {
        lock (_lock)
        {
            intervalTicks = _intervalTicks;
            if (_valueTicks <= 0)
            {
                valueTicks = 0;
                return;
            }

            var scheduler = _scheduler ?? task.CommonKernel;
            var remain = _valueTicks - scheduler.CurrentTick;
            valueTicks = remain < 0 ? 0 : remain;
        }
    }

    private void TimerCallback()
    {
        List<Action> toWake;
        lock (_lock)
        {
            _expirations++;

            if (_intervalTicks > 0)
            {
                _valueTicks += _intervalTicks;
                var scheduler = _scheduler;
                if (scheduler != null)
                {
                    var delay = Math.Max(0, _valueTicks - scheduler.CurrentTick);
                    _timer = scheduler.ScheduleTimer(delay, TimerCallback);
                }
            }
            else
            {
                _valueTicks = 0;
                _timer = null;
            }

            toWake = [.._waiters];
            _waiters.Clear();
        }

        foreach (var w in toWake) w();
    }

    protected internal override int ReadSpan(LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        lock (_lock)
        {
            if (_expirations == 0)
                // Linux timerfd_create(2): blocking reads wait until one or more expirations occur.
                // Return EAGAIN here so the generic VFS read path can invoke WaitForRead().
                return -(int)Errno.EAGAIN;

            BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], _expirations);
            _expirations = 0;
            return 8;
        }
    }

    protected internal override int WriteSpan(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EINVAL;
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                revents |= LinuxConstants.POLLIN;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        var registration = RegisterWaitHandle(file, callback, events);
        return registration != null;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
            {
                callback();
                return NoopWaitRegistration.Instance;
            }

            _waiters.Add(callback);
        }

        return new CallbackRegistration(this, _waiters, callback);
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task)
    {
        lock (_lock)
        {
            if (_expirations > 0)
                return AwaitResult.Completed;
        }

        // Linux timerfd_create(2): reads sleep until the timer has one or more expirations queued.
        return await new CallbackWaitAwaitable(task,
            callback => RegisterWaitHandle(file, callback, LinuxConstants.POLLIN));
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly Action _callback;
        private readonly TimerFdInode _owner;
        private List<Action>? _waiters;

        public CallbackRegistration(TimerFdInode owner, List<Action> waiters, Action callback)
        {
            _owner = owner;
            _waiters = waiters;
            _callback = callback;
        }

        public void Dispose()
        {
            var waiters = Interlocked.Exchange(ref _waiters, null);
            if (waiters == null) return;
            lock (_owner._lock)
            {
                waiters.Remove(_callback);
            }
        }
    }
}

internal readonly struct CallbackWaitAwaitable
{
    private readonly Func<Action, IDisposable?> _register;
    private readonly FiberTask _task;

    public CallbackWaitAwaitable(FiberTask task, Func<Action, IDisposable?> register)
    {
        _task = task;
        _register = register;
    }

    public CallbackWaitAwaiter GetAwaiter()
    {
        return new CallbackWaitAwaiter(_task, _register);
    }
}

internal readonly struct CallbackWaitAwaiter : INotifyCompletion
{
    private readonly Func<Action, IDisposable?> _register;
    private readonly FiberTask _task;
    private readonly FiberTask.WaitToken _token;

    public CallbackWaitAwaiter(FiberTask task, Func<Action, IDisposable?> register)
    {
        _task = task;
        _register = register;
        _token = task.BeginWaitToken();
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            return;

        var state = new CallbackWaitOperation(continuation, operation);
        state.TryRegister(_register(state.OnReady));
        _task.ArmInterruptingSignalSafetyNet(_token, state.OnSignal);
    }

    public AwaitResult GetResult()
    {
        var reason = _task.CompleteWaitToken(_token);
        return reason == WakeReason.None || reason == WakeReason.Event
            ? AwaitResult.Completed
            : AwaitResult.Interrupted;
    }

    private sealed class CallbackWaitOperation
    {
        private readonly TaskAsyncOperationHandle _operation;

        public CallbackWaitOperation(Action continuation, TaskAsyncOperationHandle operation)
        {
            _operation = operation;
            _operation.TryInitialize(continuation);
        }

        public void TryRegister(IDisposable? registration)
        {
            _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
        }

        public void OnReady()
        {
            _operation.TryComplete(WakeReason.Event);
        }

        public void OnSignal()
        {
            _operation.TryComplete(WakeReason.Signal);
        }
    }
}
