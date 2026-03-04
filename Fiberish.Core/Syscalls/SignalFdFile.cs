using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public class SignalFdInode : TmpfsInode
{
    private readonly object _lock = new();
    private readonly List<Action> _waiters = [];
    private ulong _sigMask;

    public SignalFdInode(ulong ino, SuperBlock superBlock, ulong sigMask) : base(ino, superBlock)
    {
        _sigMask = sigMask;
    }

    public void SetMask(ulong sigMask)
    {
        lock (_lock)
        {
            _sigMask = sigMask;
        }
    }

    public override int Read(Fiberish.VFS.LinuxFile file, Span<byte> buffer, long offset)
    {
        // struct signalfd_siginfo is 128 bytes
        if (buffer.Length < 128) return -(int)Errno.EINVAL;

        lock (_lock)
        {
            var task = KernelScheduler.Current?.CurrentTask;
            if (task == null) return -(int)Errno.EINVAL;

            SigInfo? sigInfo = null;
            lock (task)
            {
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
            }

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
        lock (_lock)
        {
            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && (events & LinuxConstants.POLLIN) != 0)
            {
                lock (task)
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
        }
        return revents;
    }

    public override bool RegisterWait(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        lock (_lock)
        {
            _waiters.Add(callback);
        }
        return true;
    }

    public override IDisposable? RegisterWaitHandle(Fiberish.VFS.LinuxFile file, Action callback, short events)
    {
        lock (_lock)
        {
            _waiters.Add(callback);
        }

        return new CallbackRegistration(_lock, _waiters, callback);
    }

    private static bool IsSignalInMask(ulong sig, ulong mask)
    {
        if (sig == 0 || sig > 64) return false;
        return (mask & (1UL << (int)(sig - 1))) != 0;
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
