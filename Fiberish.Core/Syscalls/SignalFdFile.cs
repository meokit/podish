using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public class SignalFdInode : TmpfsInode
{
    private readonly List<Action> _waiters = [];
    private ulong _sigMask;
    private FiberTask? _hookedTask;
    private int _openCount;

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

    public SignalFdInode(ulong ino, SuperBlock superBlock, ulong sigMask) : base(ino, superBlock)
    {
        _sigMask = sigMask;
    }

    public void SetMask(ulong sigMask)
    {
        using (EnterStateScope())
        {
            _sigMask = sigMask;
        }
    }

    public override void Open(Fiberish.VFS.LinuxFile file)
    {
        Interlocked.Increment(ref _openCount);
    }

    public override void Release(Fiberish.VFS.LinuxFile file)
    {
        if (Interlocked.Decrement(ref _openCount) == 0)
        {
            using (EnterStateScope())
            {
                if (_hookedTask != null)
                {
                    _hookedTask.SignalPosted -= OnTaskSignalPosted;
                    _hookedTask = null;
                }
                _waiters.Clear();
            }
        }

        base.Release(file);
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
    {
        // struct signalfd_siginfo is 128 bytes
        if (buffer.Length < 128) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            var task = KernelScheduler.Current?.CurrentTask;
            if (task == null) return -(int)Errno.EINVAL;

            SigInfo? sigInfo = null;
            int pendingMatched = 0;
            if (task.PendingSignals > 0)
            {
                for (int i = 1; i <= 64; i++)
                {
                    if ((task.PendingSignals & (1UL << (i - 1))) != 0)
                    {
                        if (IsSignalInMask((ulong)i, _sigMask))
                        {
                            pendingMatched = i;
                            break;
                        }
                    }
                }
            }

            if (pendingMatched == 0)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return 0; // Simulated block
            }

            sigInfo = task.DequeueSignalUnsafe(pendingMatched);

            if (!sigInfo.HasValue)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                return 0;
            }

            var info = sigInfo.Value;
            buffer.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0, 4), (uint)info.Signo);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4, 4), info.Errno);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8, 4), info.Code);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12, 4), (uint)info.Pid);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(16, 4), info.Uid);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(20, 4), info.Fd);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(24, 4), (uint)info.TimerId);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(28, 4), (uint)info.Band);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(32, 4), (uint)info.Overrun);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(36, 4), (uint)info.Status);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(40, 8), (ulong)info.Utime);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(48, 8), (ulong)info.Stime);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(56, 8), info.Value);

            return 128;
        }
    }

    public override int Write(Fiberish.VFS.LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EINVAL; // Invalid argument to write to signalfd
    }

    public override short Poll(Fiberish.VFS.LinuxFile file, short events)
    {
        short revents = 0;
        using (EnterStateScope())
        {
            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && (events & LinuxConstants.POLLIN) != 0)
            {
                for (int i = 1; i <= 64; i++)
                {
                    if ((task.PendingSignals & (1UL << (i - 1))) != 0)
                    {
                        if (IsSignalInMask((ulong)i, _sigMask))
                        {
                            revents |= (short)LinuxConstants.POLLIN;
                            break;
                        }
                    }
                }
            }
        }
        return revents;
    }

    public override bool RegisterWait(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        var task = KernelScheduler.Current?.CurrentTask;
        using (EnterStateScope())
        {
            EnsureTaskHooked(task);
            if (task != null && HasPendingMatchedSignalUnsafe(task))
                return false;
            _waiters.Add(callback);
        }
        return true;
    }

    public override IDisposable? RegisterWaitHandle(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        var task = KernelScheduler.Current?.CurrentTask;
        using (EnterStateScope())
        {
            EnsureTaskHooked(task);
            if (task != null && HasPendingMatchedSignalUnsafe(task))
            {
                callback();
                return NoopWaitRegistration.Instance;
            }
            _waiters.Add(callback);
        }

        return new CallbackRegistration(this, _waiters, callback);
    }

    private void OnTaskSignalPosted(int sig)
    {
        ulong mask;
        using (EnterStateScope())
        {
            mask = _sigMask;
        }
        if (IsSignalInMask((ulong)sig, mask))
        {
            NotifyWaiters();
        }
    }

    private void NotifyWaiters()
    {
        Action[] toWake;
        using (EnterStateScope())
        {
            toWake = [.._waiters];
            _waiters.Clear();
        }
        foreach (var action in toWake)
        {
            action();
        }
    }

    private void EnsureTaskHooked(FiberTask? task)
    {
        if (task == null || ReferenceEquals(_hookedTask, task))
            return;
        if (_hookedTask != null)
        {
            _hookedTask.SignalPosted -= OnTaskSignalPosted;
        }
        _hookedTask = task;
        _hookedTask.SignalPosted += OnTaskSignalPosted;
    }

    private bool HasPendingMatchedSignalUnsafe(FiberTask task)
    {
        if (task.PendingSignals == 0)
            return false;
        for (var i = 1; i <= 64; i++)
        {
            if ((task.PendingSignals & (1UL << (i - 1))) == 0)
                continue;
            if (IsSignalInMask((ulong)i, _sigMask))
                return true;
        }
        return false;
    }

    private static bool IsSignalInMask(ulong sig, ulong mask)
    {
        if (sig == 0 || sig > 64) return false;
        return (mask & (1UL << (int)(sig - 1))) != 0;
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly SignalFdInode _owner;
        private List<Action>? _waiters;
        private readonly Action _callback;

        public CallbackRegistration(SignalFdInode owner, List<Action> waiters, Action callback)
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
