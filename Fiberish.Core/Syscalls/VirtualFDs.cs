using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public class EventFdInode : TmpfsInode
{
    private const ulong MaxCounter = ulong.MaxValue - 1;
    private readonly bool _isSemaphore;
    private readonly List<Action> _readWaiters = [];
    private readonly List<Action> _writeWaiters = [];
    private ulong _counter;

    public EventFdInode(ulong ino, SuperBlock superBlock, ulong initval, FileFlags flags) : base(ino, superBlock)
    {
        _counter = initval;
        _isSemaphore = (flags & (FileFlags)LinuxConstants.EFD_SEMAPHORE) != 0;
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        return default;
    }

    protected internal override int ReadSpan(LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
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

            if (wasFull)
                NotifyWriteWaiters();

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
            if (MaxCounter - _counter < add)
                // Linux eventfd(2): writes that would overflow block unless O_NONBLOCK is set.
                // Return EAGAIN so the generic VFS write path can wait for POLLOUT.
                return -(int)Errno.EAGAIN;

            var wasEmpty = _counter == 0;
            _counter += add;
            if (wasEmpty)
                NotifyReadWaiters();
            return 8;
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        using (EnterStateScope())
        {
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
        if (IsReadyForEvents(events))
        {
            callback();
            return NoopWaitRegistration.Instance;
        }

        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0)
                _readWaiters.Add(callback);
            if ((events & LinuxConstants.POLLOUT) != 0)
                _writeWaiters.Add(callback);
        }

        return new CallbackRegistration(this, callback, events);
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task)
    {
        using (EnterStateScope())
        {
            if (_counter > 0)
                return AwaitResult.Completed;
        }

        // Linux eventfd(2): blocking reads sleep until the counter becomes nonzero.
        return await new CallbackWaitAwaitable(task,
            callback => RegisterWaitHandle(file, callback, LinuxConstants.POLLIN));
    }

    public override async ValueTask<AwaitResult> WaitForWrite(LinuxFile file, FiberTask task,
        int minWritableBytes = 1)
    {
        _ = minWritableBytes;
        using (EnterStateScope())
        {
            if (_counter < MaxCounter)
                return AwaitResult.Completed;
        }

        // Linux eventfd(2): POLLOUT means a write of at least 1 is possible without blocking.
        return await new CallbackWaitAwaitable(task,
            callback => RegisterWaitHandle(file, callback, LinuxConstants.POLLOUT));
    }

    private void NotifyReadWaiters()
    {
        Action[] toWake;
        using (EnterStateScope())
        {
            toWake = [.._readWaiters];
            _readWaiters.Clear();
        }

        foreach (var action in toWake) action();
    }

    private void NotifyWriteWaiters()
    {
        Action[] toWake;
        using (EnterStateScope())
        {
            toWake = [.._writeWaiters];
            _writeWaiters.Clear();
        }

        foreach (var action in toWake) action();
    }

    private bool IsReadyForEvents(short events)
    {
        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _counter > 0)
                return true;
            if ((events & LinuxConstants.POLLOUT) != 0 && _counter < MaxCounter)
                return true;
            return false;
        }
    }

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly Action _callback;
        private readonly EventFdInode _owner;
        private readonly short _events;
        private int _disposed;

        public CallbackRegistration(EventFdInode owner, Action callback, short events)
        {
            _owner = owner;
            _callback = callback;
            _events = events;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using (_owner.EnterStateScope())
            {
                if ((_events & LinuxConstants.POLLIN) != 0)
                    _owner._readWaiters.Remove(_callback);
                if ((_events & LinuxConstants.POLLOUT) != 0)
                    _owner._writeWaiters.Remove(_callback);
            }
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