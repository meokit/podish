using System.Buffers.Binary;
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
    private readonly object _lock = new();

    public EventFdInode(ulong ino, SuperBlock superBlock, ulong initval, FileFlags flags) : base(ino, superBlock)
    {
        _counter = initval;
        _isSemaphore = (flags & (FileFlags)LinuxConstants.EFD_SEMAPHORE) != 0;
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        lock (_lock)
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

        lock (_lock)
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
        lock (_lock)
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

        lock (_lock)
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

        lock (_lock)
        {
            _waiters.Add(callback);
        }

        return new CallbackRegistration(_lock, _waiters, callback);
    }

    private void NotifyWaiters()
    {
        Action[] toWake;
        lock (_lock)
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
        lock (_lock)
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
        private readonly object _lock;
        private List<Action>? _waiters;
        private readonly Action _callback;

        public CallbackRegistration(object @lock, List<Action> waiters, Action callback)
        {
            _lock = @lock;
            _waiters = waiters;
            _callback = callback;
        }

        public void Dispose()
        {
            var waiters = Interlocked.Exchange(ref _waiters, null);
            if (waiters == null) return;
            lock (_lock)
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
    private readonly object _lock = new();
    private readonly List<Action> _waiters = [];
    
    private long _intervalTicks;
    private long _valueTicks; // Absolute expiration tick

    public TimerFdInode(ulong ino, SuperBlock superBlock) : base(ino, superBlock)
    {
    }

    public void SetTime(long intervalTicks, long valueTicks, bool isAbsolute)
    {
        lock (_lock)
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
        lock (_lock)
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
        lock (_lock)
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
        lock (_lock)
        {
            toWake = [.._waiters];
            _waiters.Clear();
        }
        foreach (var w in toWake) w();
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
    {
        if (buffer.Length < 8) return -(int)Errno.EINVAL;

        lock (_lock)
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
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                revents |= LinuxConstants.POLLIN;
        }
        return revents;
    }

    public override bool RegisterWait(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        lock (_lock)
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
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _expirations > 0)
                return null;
            _waiters.Add(callback);
        }

        return new CallbackRegistration(_lock, _waiters, callback);
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly object _lock;
        private List<Action>? _waiters;
        private readonly Action _callback;

        public CallbackRegistration(object @lock, List<Action> waiters, Action callback)
        {
            _lock = @lock;
            _waiters = waiters;
            _callback = callback;
        }

        public void Dispose()
        {
            var waiters = Interlocked.Exchange(ref _waiters, null);
            if (waiters == null) return;
            lock (_lock)
            {
                waiters.Remove(_callback);
            }
        }
    }
}
