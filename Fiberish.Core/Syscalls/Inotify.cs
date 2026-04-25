using System.Buffers.Binary;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public sealed class InotifyInode : TmpfsInode
{
    private const int InotifyEventHeaderSize = 16;

    private readonly Lock _lock = new();
    private readonly Queue<byte[]> _pendingEvents = new();
    private readonly Dictionary<int, WatchState> _watchesByDescriptor = [];
    private readonly Dictionary<string, int> _watchDescriptorsByPath = new(StringComparer.Ordinal);
    private readonly List<Action> _waiters = [];

    private int _nextWatchDescriptor = 1;

    public InotifyInode(ulong ino, SuperBlock superBlock) : base(ino, superBlock)
    {
    }

    public int AddWatch(string pathKey, uint mask, bool isDirectory)
    {
        lock (_lock)
        {
            if (_watchDescriptorsByPath.TryGetValue(pathKey, out var existingWd) &&
                _watchesByDescriptor.TryGetValue(existingWd, out var existingWatch))
            {
                if ((mask & LinuxConstants.IN_MASK_CREATE) != 0)
                    return -(int)Errno.EEXIST;

                var nextMask = (mask & LinuxConstants.IN_MASK_ADD) != 0
                    ? existingWatch.Mask | mask
                    : mask;
                _watchesByDescriptor[existingWd] = existingWatch with { Mask = nextMask, IsDirectory = isDirectory };
                return existingWd;
            }

            var wd = _nextWatchDescriptor++;
            _watchesByDescriptor[wd] = new WatchState(wd, pathKey, mask, isDirectory);
            _watchDescriptorsByPath[pathKey] = wd;
            return wd;
        }
    }

    public int RemoveWatch(int wd)
    {
        List<Action>? waitersToNotify = null;
        lock (_lock)
        {
            if (!_watchesByDescriptor.Remove(wd, out var watch))
                return -(int)Errno.EINVAL;

            _watchDescriptorsByPath.Remove(watch.PathKey);
            waitersToNotify = EnqueueEventLocked(wd, LinuxConstants.IN_IGNORED, 0, null);
        }

        NotifyWaiters(waitersToNotify);
        return 0;
    }

    public override int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset)
    {
        _ = task;
        if (buffer.Length == 0)
            return 0;

        lock (_lock)
        {
            if (_pendingEvents.Count == 0)
                return -(int)Errno.EAGAIN;

            var nextEvent = _pendingEvents.Peek();
            if (buffer.Length < nextEvent.Length)
                return -(int)Errno.EINVAL;

            var written = 0;
            while (_pendingEvents.Count > 0)
            {
                var pending = _pendingEvents.Peek();
                if (written + pending.Length > buffer.Length)
                    break;

                pending.CopyTo(buffer[written..]);
                written += pending.Length;
                _pendingEvents.Dequeue();
            }
            return written;
        }
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile file, ReadOnlySpan<byte> buffer,
        long offset)
    {
        _ = task;
        return -(int)Errno.EBADF;
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) != 0 && _pendingEvents.Count > 0)
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
        var invokeNow = false;
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) == 0)
                return null;

            if (_pendingEvents.Count > 0)
                invokeNow = true;
            else
                _waiters.Add(callback);

            if (!invokeNow)
                return new CallbackRegistration(this, _waiters, callback);
        }

        callback();
        return NoopWaitRegistration.Instance;
    }

    public override IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile file, Action callback, short events)
    {
        lock (_lock)
        {
            if ((events & LinuxConstants.POLLIN) == 0)
                return null;

            _waiters.Add(callback);
            return new CallbackRegistration(this, _waiters, callback);
        }
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task)
    {
        lock (_lock)
        {
            if (_pendingEvents.Count > 0)
                return AwaitResult.Completed;
        }

        return await new CallbackWaitAwaitable(task,
            callback => RegisterWaitHandle(file, callback, LinuxConstants.POLLIN));
    }

    private List<Action>? EnqueueEventLocked(int wd, uint mask, uint cookie, string? name)
    {
        var encodedName = string.IsNullOrEmpty(name) ? null : Encoding.UTF8.GetBytes(name);
        var nameFieldLength = encodedName == null ? 0 : Align4(encodedName.Length + 1);
        var payload = new byte[InotifyEventHeaderSize + nameFieldLength];

        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), wd);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), cookie);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), (uint)nameFieldLength);

        if (encodedName != null)
        {
            encodedName.CopyTo(payload.AsSpan(InotifyEventHeaderSize, encodedName.Length));
            payload[InotifyEventHeaderSize + encodedName.Length] = 0;
        }

        var wasEmpty = _pendingEvents.Count == 0;
        _pendingEvents.Enqueue(payload);
        if (!wasEmpty)
            return null;

        List<Action> waiters = [.._waiters];
        _waiters.Clear();
        return waiters;
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private static void NotifyWaiters(List<Action>? waiters)
    {
        if (waiters == null)
            return;

        foreach (var waiter in waiters)
            waiter();
    }

    private sealed class CallbackRegistration : IDisposable
    {
        private readonly Action _callback;
        private readonly InotifyInode _owner;
        private List<Action>? _waiters;

        public CallbackRegistration(InotifyInode owner, List<Action> waiters, Action callback)
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

    private readonly record struct WatchState(int WatchDescriptor, string PathKey, uint Mask, bool IsDirectory);
}