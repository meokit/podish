using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public class EventFdInode : TmpfsInode
{
    private readonly bool _isSemaphore;
    private readonly List<Action> _waiters = [];
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

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            if (_counter == 0)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return 0; // Simulated block - caller should use RegisterWait / PollAwaiter
            }

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

            // Notify waiters that it's writable
            NotifyWaiters();

            return 8;
        }
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        var add = BinaryPrimitives.ReadUInt64LittleEndian(buffer[..8]);
        if (add == 0xFFFFFFFFFFFFFFFF) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            if (ulong.MaxValue - _counter < add)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return 0; // Simulated block
            }

            _counter += add;
            NotifyWaiters();
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
            if ((events & LinuxConstants.POLLOUT) != 0 && _counter < ulong.MaxValue - 1)
                revents |= LinuxConstants.POLLOUT;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        if (IsReadyForEvents(events))
            return false;

        using (EnterStateScope())
        {
            _waiters.Add(callback);
        }

        return true;
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
            _waiters.Add(callback);
        }

        return new CallbackRegistration(this, _waiters, callback);
    }

    private void NotifyWaiters()
    {
        Action[] toWake;
        using (EnterStateScope())
        {
            toWake = [.._waiters];
            _waiters.Clear(); // They will re-register if they poll again
        }

        foreach (var action in toWake) action();
    }

    private bool IsReadyForEvents(short events)
    {
        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _counter > 0)
                return true;
            if ((events & LinuxConstants.POLLOUT) != 0 && _counter < ulong.MaxValue - 1)
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
        private List<Action>? _waiters;

        public CallbackRegistration(EventFdInode owner, List<Action> waiters, Action callback)
        {
            _owner = owner;
            _waiters = waiters;
            _callback = callback;
        }

        public void Dispose()
        {
            var waiters = Interlocked.Exchange(ref _waiters, null);
            if (waiters == null) return;
            using (_owner.EnterStateScope())
            {
                waiters.Remove(_callback);
            }
        }
    }
}

public class TimerFdInode : TmpfsInode
{
    private readonly object _lock = new();
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

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        lock (_lock)
        {
            if (_expirations == 0)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return
                    0; // Normally we'd block here if VFS supported sync blocking directly, but for now we expect users to use poll/select before reading.
            }

            BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], _expirations);
            _expirations = 0;
            return 8;
        }
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
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
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                // Already signaled
                return false;

            _waiters.Add(callback);
            return true;
        }
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                return null;
            _waiters.Add(callback);
        }

        return new CallbackRegistration(this, _waiters, callback);
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