using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public class SignalFdInode : TmpfsInode, ITaskWaitSource
{
    private ulong _sigMask;

    public SignalFdInode(ulong ino, SuperBlock superBlock, ulong sigMask) : base(ino, superBlock)
    {
        _sigMask = sigMask;
    }

    bool ITaskWaitSource.RegisterWait(LinuxFile file, FiberTask task, Action callback, short events)
    {
        return RegisterWait(task, callback, events);
    }

    IDisposable? ITaskWaitSource.RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        return RegisterWaitHandle(task, callback, events);
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        return default;
    }

    public void SetMask(ulong sigMask)
    {
        using (EnterStateScope())
        {
            _sigMask = sigMask;
        }
    }

    public int Read(FiberTask task, LinuxFile file, Span<byte> buffer)
    {
        if (buffer.Length < 128) return -(int)Errno.EINVAL;

        using (EnterStateScope())
        {
            var sigInfo = TryConsumeNextSignalUnsafe(task);
            if (!sigInfo.HasValue)
                return (file.Flags & FileFlags.O_NONBLOCK) != 0 ? -(int)Errno.EAGAIN : 0;

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

    public short Poll(FiberTask task, short events)
    {
        if ((events & LinuxConstants.POLLIN) == 0)
            return 0;

        using (EnterStateScope())
        {
            return HasPendingMatchedSignalUnsafe(task) ? (short)LinuxConstants.POLLIN : (short)0;
        }
    }

    public bool RegisterWait(FiberTask task, Action callback, short events)
    {
        return RegisterWaitHandle(task, callback, events) != null;
    }

    public IDisposable? RegisterWaitHandle(FiberTask task, Action callback, short events)
    {
        if ((events & LinuxConstants.POLLIN) == 0)
            return null;

        using (EnterStateScope())
        {
            if (HasPendingMatchedSignalUnsafe(task))
            {
                callback();
                return NoopWaitRegistration.Instance;
            }
        }

        return new SignalWaitRegistration(task, GetMask(), callback);
    }

    public SignalFdAwaitable WaitAsync(FiberTask task)
    {
        return new SignalFdAwaitable(this, task);
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        return -(int)Errno.EINVAL;
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EINVAL;
    }

    public override short Poll(LinuxFile file, short events)
    {
        return 0;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        return null;
    }

    private ulong GetMask()
    {
        using (EnterStateScope())
        {
            return _sigMask;
        }
    }

    private SigInfo? TryConsumeNextSignalUnsafe(FiberTask task)
    {
        return task.TryTakeVisibleSignalForSignalfd(GetMask(), out var info) ? info : null;
    }

    private bool HasPendingMatchedSignalUnsafe(FiberTask task)
    {
        return task.HasVisiblePendingSignalForSignalfd(GetMask());
    }

    private int FindNextMatchedSignalUnsafe(FiberTask task)
    {
        var visiblePending = task.GetVisiblePendingSignals();
        if (visiblePending == 0)
            return 0;

        for (var i = 1; i <= 64; i++)
        {
            if ((visiblePending & (1UL << (i - 1))) == 0)
                continue;
            if (i == (int)Signal.SIGKILL || i == (int)Signal.SIGSTOP)
                continue;
            if (i == (int)Signal.SIGBUS || i == (int)Signal.SIGFPE || i == (int)Signal.SIGILL ||
                i == (int)Signal.SIGSEGV)
                continue;
            if (IsSignalInMask((ulong)i, _sigMask))
                return i;
        }

        return 0;
    }

    private static bool IsSignalInMask(ulong sig, ulong mask)
    {
        if (sig == 0 || sig > 64) return false;
        return (mask & (1UL << (int)(sig - 1))) != 0;
    }

    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class SignalWaitRegistration : IDisposable
    {
        private readonly Action _callback;
        private readonly ulong _mask;
        private readonly FiberTask _task;
        private int _disposed;

        public SignalWaitRegistration(FiberTask task, ulong mask, Action callback)
        {
            _task = task;
            _mask = mask;
            _callback = callback;
            _task.SignalPosted += OnSignalPosted;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _task.SignalPosted -= OnSignalPosted;
        }

        private void OnSignalPosted(int sig)
        {
            if (!IsSignalInMask((ulong)sig, _mask))
                return;
            Dispose();
            _callback();
        }
    }
}

public readonly struct SignalFdAwaitable
{
    private readonly SignalFdInode _inode;
    private readonly FiberTask _task;

    public SignalFdAwaitable(SignalFdInode inode, FiberTask task)
    {
        _inode = inode;
        _task = task;
    }

    public SignalFdAwaiter GetAwaiter()
    {
        return new SignalFdAwaiter(_inode, _task);
    }
}

public struct SignalFdAwaiter : INotifyCompletion
{
    private readonly SignalFdInode _inode;
    private readonly FiberTask _task;
    private FiberTask.WaitToken? _token;

    public SignalFdAwaiter(SignalFdInode inode, FiberTask task)
    {
        _inode = inode;
        _task = task;
    }

    public bool IsCompleted => (_inode.Poll(_task, LinuxConstants.POLLIN) & LinuxConstants.POLLIN) != 0;

    public void OnCompleted(Action continuation)
    {
        _token = _task.BeginWaitToken();
        if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            return;

        var state = new SignalFdWaitOperation(_task, continuation, operation);
        state.TryRegister(_inode.RegisterWaitHandle(_task, state.OnReadable, LinuxConstants.POLLIN));
    }

    public AwaitResult GetResult()
    {
        if (_token == null) return AwaitResult.Completed;

        _task.CompleteWaitToken(_token);
        return AwaitResult.Completed;
    }

    private sealed class SignalFdWaitOperation
    {
        private readonly TaskAsyncOperationHandle _operation;

        public SignalFdWaitOperation(FiberTask task, Action continuation, TaskAsyncOperationHandle operation)
        {
            _operation = operation;
            _operation.TryInitialize(continuation);
        }

        public void TryRegister(IDisposable? registration)
        {
            _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
        }

        public void OnReadable()
        {
            _operation.TryComplete(WakeReason.IO);
        }
    }
}