using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public class EventFdInode : TmpfsInode
{
    private ulong _counter;
    private readonly bool _isSemaphore;
    private readonly List<Action> _waiters = [];

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        KernelScheduler.Current?.AssertSchedulerThread(caller);
        return default;
    }

    public EventFdInode(ulong ino, SuperBlock superBlock, ulong initval, FileFlags flags) : base(ino, superBlock)
    {
        _counter = initval;
        _isSemaphore = (flags & (FileFlags)LinuxConstants.EFD_SEMAPHORE) != 0;
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
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

    public override int Write(Fiberish.VFS.LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
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

    public override short Poll(Fiberish.VFS.LinuxFile file, short events)
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

    public override bool RegisterWait(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        if (IsReadyForEvents(events))
            return false;

        using (EnterStateScope())
        {
            _waiters.Add(callback);
        }
        return true;
    }

    public override IDisposable? RegisterWaitHandle(Fiberish.VFS.LinuxFile file, Action callback, short events)
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
        foreach (var action in toWake)
        {
            action();
        }
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

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly EventFdInode _owner;
        private List<Action>? _waiters;
        private readonly Action _callback;

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
    private ulong _expirations;
    private Fiberish.Core.Timer? _timer;
    private readonly List<Action> _waiters = [];
    
    private long _intervalTicks;
    private long _valueTicks; // Absolute expiration tick

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        KernelScheduler.Current?.AssertSchedulerThread(caller);
        return default;
    }

    public TimerFdInode(ulong ino, SuperBlock superBlock) : base(ino, superBlock)
    {
    }

    public void SetTime(long intervalTicks, long valueTicks, bool isAbsolute)
    {
        using (EnterStateScope())
        {
            _timer?.Cancel();
            _timer = null;

            _intervalTicks = intervalTicks;
            
            if (valueTicks > 0)
            {
                var scheduler = KernelScheduler.Current!;
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

    public void GetTime(out long intervalTicks, out long valueTicks)
    {
        using (EnterStateScope())
        {
            intervalTicks = _intervalTicks;
            if (_valueTicks > 0)
            {
                var scheduler = KernelScheduler.Current;
                if (scheduler == null) 
                {
                    valueTicks = 0;
                }
                else
                {
                    var remain = _valueTicks - scheduler.CurrentTick;
                    valueTicks = remain < 0 ? 0 : remain;
                }
            }
            else
            {
                valueTicks = 0;
            }
        }
    }

    private void TimerCallback()
    {
        using (EnterStateScope())
        {
            _expirations++;
            
            if (_intervalTicks > 0)
            {
                _valueTicks += _intervalTicks;
                var scheduler = KernelScheduler.Current;
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
        }
        
        List<Action> toWake;
        using (EnterStateScope())
        {
            toWake = [.._waiters];
            _waiters.Clear();
        }
        foreach (var w in toWake) w();
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            if (_expirations == 0)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return 0; // Normally we'd block here if VFS supported sync blocking directly, but for now we expect users to use poll/select before reading.
            }

            BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], _expirations);
            _expirations = 0;
            return 8;
        }
    }

    public override int Write(Fiberish.VFS.LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EINVAL; 
    }

    public override short Poll(Fiberish.VFS.LinuxFile file, short events)
    {
        short revents = 0;
        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                revents |= LinuxConstants.POLLIN;
        }
        return revents;
    }

    public override bool RegisterWait(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
            {
                // Already signaled
                return false;
            }
            _waiters.Add(callback);
            return true;
        }
    }

    public override IDisposable? RegisterWaitHandle(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        using (EnterStateScope())
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                return null;
            _waiters.Add(callback);
        }

        return new CallbackRegistration(this, _waiters, callback);
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly TimerFdInode _owner;
        private List<Action>? _waiters;
        private readonly Action _callback;

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
            using (_owner.EnterStateScope())
            {
                waiters.Remove(_callback);
            }
        }
    }
}
