using System.Buffers;
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
    private readonly Dictionary<(SuperBlock Sb, ulong Ino), int> _watchDescriptorsByInode = [];
    private readonly List<Action> _waiters = [];
    private readonly bool _instanceSlotHeld;
    private int _instanceSlotReleased;

    private int _nextWatchDescriptor = 1;
    private bool _overflowQueued;

    // Tail of the pending queue. Consecutive identical events are coalesced without
    // allocating a new payload or walking the queue.
    private bool _hasLastEvent;
    private int _lastEventWd;
    private uint _lastEventMask;
    private uint _lastEventCookie;
    private byte[]? _lastEventPayload;

    public InotifyInode(ulong ino, SuperBlock superBlock, int ownerUid = 0) : base(ino, superBlock)
    {
        Runtime = superBlock.MemoryContext.Inotify;
        OwnerUid = ownerUid;
        _instanceSlotHeld = Runtime.TryAcquireInstance(ownerUid);
    }

    public InotifyRuntimeState Runtime { get; }
    public int OwnerUid { get; }
    public bool InstanceSlotHeld => _instanceSlotHeld;

    public int AddWatch(uint mask, bool isDirectory, SuperBlock watchSb, ulong watchIno, Inode? targetInode = null)
    {
        if (watchIno == 0)
            return -(int)Errno.EINVAL;

        lock (_lock)
        {
            // Linux identifies a watch by inode, not by path: adding a watch on another
            // hard link of the same inode updates the existing watch and returns its wd.
            var key = (watchSb, watchIno);
            if (_watchDescriptorsByInode.TryGetValue(key, out var existingWd) &&
                _watchesByDescriptor.TryGetValue(existingWd, out var existingWatch))
            {
                if ((mask & LinuxConstants.IN_MASK_CREATE) != 0)
                    return -(int)Errno.EEXIST;

                var nextMask = (mask & LinuxConstants.IN_MASK_ADD) != 0
                    ? existingWatch.Mask | mask
                    : mask;
                // The registry key and descriptor do not change, so no re-registration.
                _watchesByDescriptor[existingWd] = existingWatch with
                {
                    Mask = nextMask, IsDirectory = isDirectory,
                    TargetInode = targetInode ?? existingWatch.TargetInode
                };
                return existingWd;
            }

            if (!Runtime.TryAcquireWatch(OwnerUid))
                return -(int)Errno.ENOSPC;

            var wd = _nextWatchDescriptor++;
            var state = new WatchState(wd, mask, isDirectory, watchSb, watchIno)
            {
                TargetInode = targetInode
            };
            _watchesByDescriptor[wd] = state;
            _watchDescriptorsByInode[key] = wd;
            // The registry belongs to the watched file's runtime, which may differ from
            // the inotify instance's runtime when mounts carry their own memory context.
            InotifyEvents.Register(this, state);
            return wd;
        }
    }

    internal void DeliverEvent(int wd, uint mask, ReadOnlySpan<byte> name, uint cookie = 0, Inode? subject = null,
        bool subjectUnlinked = false)
    {
        List<Action>? waiters = null;
        lock (_lock)
        {
            if (!_watchesByDescriptor.TryGetValue(wd, out var watch))
                return;

            // Only add the directory bit when the underlying event bits actually matched;
            // IN_ISDIR alone must never be reported for a watch that lacks the mask.
            var effective = mask & (watch.Mask | LinuxConstants.IN_IGNORED | LinuxConstants.IN_UNMOUNT);
            effective |= mask & LinuxConstants.IN_ISDIR;
            var deliverable = (effective & ~LinuxConstants.IN_ISDIR) != 0;
            var deleteSelf = (mask & LinuxConstants.IN_DELETE_SELF) != 0;
            if (!deliverable && !deleteSelf)
                return;

            // IN_EXCL_UNLINK applies to events delivered through a parent directory watch
            // for a child dentry that has been removed from the namespace. A direct watch
            // on the inode still reports events while open descriptors exist.
            if (deliverable && subjectUnlinked &&
                (watch.Mask & LinuxConstants.IN_EXCL_UNLINK) != 0 &&
                (mask & (LinuxConstants.IN_ACCESS | LinuxConstants.IN_OPEN | LinuxConstants.IN_MODIFY |
                         LinuxConstants.IN_CLOSE_WRITE | LinuxConstants.IN_CLOSE_NOWRITE)) != 0)
                deliverable = false;

            if (deliverable)
                waiters = EnqueueEventLocked(wd, effective, cookie, name);

            // Retirement is driven by the deletion, not by the watch mask: Linux always
            // queues IN_IGNORED when the watched object disappears, even if the caller
            // did not subscribe to IN_DELETE_SELF. ONESHOT retires after a delivered event.
            var removeWatch = deleteSelf || (deliverable && (watch.Mask & LinuxConstants.IN_ONESHOT) != 0);
            if (removeWatch)
            {
                _watchesByDescriptor.Remove(wd);
                UnindexWatchLocked(watch);
                var ignoredWaiters = EnqueueEventLocked(wd, LinuxConstants.IN_IGNORED, 0, ReadOnlySpan<byte>.Empty);
                if (ignoredWaiters != null) waiters = ignoredWaiters;
            }
        }

        NotifyWaiters(waiters);
    }

    internal void UnregisterAllWatches()
    {
        lock (_lock)
        {
            foreach (var watch in _watchesByDescriptor.Values)
                InotifyEvents.Unregister(this, watch);
            if (_watchesByDescriptor.Count > 0)
                Runtime.ReleaseWatches(OwnerUid, _watchesByDescriptor.Count);
            _watchesByDescriptor.Clear();
            _watchDescriptorsByInode.Clear();
        }
    }

    public override void Release(LinuxFile linuxFile)
    {
        ReleaseUserLimits();
        base.Release(linuxFile);
    }

    protected override void OnEvictCache()
    {
        ReleaseUserLimits();
        base.OnEvictCache();
    }

    private void ReleaseUserLimits()
    {
        UnregisterAllWatches();
        if (_instanceSlotHeld && Interlocked.Exchange(ref _instanceSlotReleased, 1) == 0)
            Runtime.ReleaseInstance(OwnerUid);
    }

    public int RemoveWatch(int wd)
    {
        List<Action>? waitersToNotify = null;
        lock (_lock)
        {
            if (!_watchesByDescriptor.Remove(wd, out var watch))
                return -(int)Errno.EINVAL;

            UnindexWatchLocked(watch);
            waitersToNotify = EnqueueEventLocked(wd, LinuxConstants.IN_IGNORED, 0, ReadOnlySpan<byte>.Empty);
        }

        NotifyWaiters(waitersToNotify);
        return 0;
    }

    private void UnindexWatchLocked(WatchState watch)
    {
        _watchDescriptorsByInode.Remove((watch.WatchSb, watch.WatchIno));
        InotifyEvents.Unregister(this, watch);
        Runtime.ReleaseWatch(OwnerUid);
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

                // Keep the overflow marker pending until it has actually been read; a
                // partial read must not allow a second IN_Q_OVERFLOW into the queue.
                if (BinaryPrimitives.ReadInt32LittleEndian(pending.AsSpan(0, 4)) == -1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(pending.AsSpan(4, 4)) == LinuxConstants.IN_Q_OVERFLOW)
                    _overflowQueued = false;
            }

            if (_pendingEvents.Count == 0)
            {
                _hasLastEvent = false;
                _lastEventPayload = null;
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

    internal string GetFdInfoLines()
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var watch in _watchesByDescriptor.Values)
            {
                var ino = watch.WatchIno != 0 ? watch.WatchIno.ToString("x") : "0";
                sb.Append("inotify wd:").Append(watch.WatchDescriptor)
                    .Append(" ino:").Append(ino)
                    .Append(" sdev:0 mask:").Append(watch.Mask.ToString("x"))
                    .Append(" ignored_mask:0 fhandle-bytes:0 fhandle-type:0 f_ver:0\n");
            }
        }

        return sb.ToString();
    }

    private List<Action>? EnqueueEventLocked(int wd, uint mask, uint cookie, ReadOnlySpan<byte> name)
    {
        // Linux coalesces consecutive identical unread events (same wd, mask, cookie and
        // name). IN_IGNORED is a lifecycle marker and is never merged.
        if (mask != LinuxConstants.IN_IGNORED && _hasLastEvent &&
            _lastEventWd == wd && _lastEventMask == mask && _lastEventCookie == cookie &&
            NameMatchesLastLocked(name))
            return null;

        if (_pendingEvents.Count >= Runtime.MaxQueuedEvents)
        {
            if (_overflowQueued)
                return null;

            _overflowQueued = true;
            var overflow = new byte[InotifyEventHeaderSize];
            BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(0, 4), -1);
            BinaryPrimitives.WriteUInt32LittleEndian(overflow.AsSpan(4, 4), LinuxConstants.IN_Q_OVERFLOW);
            var wasEmptyOverflow = _pendingEvents.Count == 0;
            _pendingEvents.Enqueue(overflow);
            SetLastEventLocked(-1, LinuxConstants.IN_Q_OVERFLOW, 0, overflow);
            if (!wasEmptyOverflow)
                return null;

            List<Action> overflowWaiters = [.._waiters];
            _waiters.Clear();
            return overflowWaiters;
        }

        // Copy the raw filesystem bytes: decoding to a string first corrupts names that
        // are not valid UTF-8 (Linux treats inotify names as opaque bytes).
        var nameFieldLength = name.IsEmpty ? 0 : Align4(name.Length + 1);
        var payload = new byte[InotifyEventHeaderSize + nameFieldLength];

        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), wd);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), cookie);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), (uint)nameFieldLength);

        if (!name.IsEmpty)
        {
            name.CopyTo(payload.AsSpan(InotifyEventHeaderSize));
            payload[InotifyEventHeaderSize + name.Length] = 0;
        }

        var wasEmpty = _pendingEvents.Count == 0;
        _pendingEvents.Enqueue(payload);
        SetLastEventLocked(wd, mask, cookie, payload);
        if (!wasEmpty)
            return null;

        List<Action> waiters = [.._waiters];
        _waiters.Clear();
        return waiters;
    }

    private bool NameMatchesLastLocked(ReadOnlySpan<byte> name)
    {
        var last = _lastEventPayload;
        if (last == null || last.Length < InotifyEventHeaderSize)
            return name.IsEmpty;

        var lastLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(12, 4));
        var lastRaw = last.AsSpan(InotifyEventHeaderSize, lastLength);
        var lastNul = lastRaw.IndexOf((byte)0);
        if (lastNul >= 0) lastRaw = lastRaw[..lastNul];
        return lastRaw.SequenceEqual(name);
    }

    private void SetLastEventLocked(int wd, uint mask, uint cookie, byte[] payload)
    {
        _hasLastEvent = true;
        _lastEventWd = wd;
        _lastEventMask = mask;
        _lastEventCookie = cookie;
        _lastEventPayload = payload;
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

    internal readonly record struct WatchState(int WatchDescriptor, uint Mask, bool IsDirectory,
        SuperBlock WatchSb, ulong WatchIno)
    {
        public Inode? TargetInode { get; init; }
    }
}

/// <summary>
///     Static facade over the inotify registry owned by the inode's runtime context.
/// </summary>
public static class InotifyEvents
{
    /// <summary>Remember that an inode lost its last directory link (IN_EXCL_UNLINK/finalization).</summary>
    public static void MarkUnlinked(Inode? inode)
    {
        if (inode?.SuperBlock == null || inode.Ino == 0)
            return;

        inode.MarkNamespaceUnlinked();
    }

    internal static void Register(InotifyInode owner, InotifyInode.WatchState state)
    {
        state.WatchSb.MemoryContext?.Inotify.Register(owner, state);
    }

    /// <summary>Called when an inode loses its last link and last runtime reference.</summary>
    public static void NotifyInodeFinalized(Inode? inode)
    {
        inode?.SuperBlock?.MemoryContext?.Inotify.NotifyInodeFinalized(inode);
    }

    internal static void Unregister(InotifyInode owner, InotifyInode.WatchState state)
    {
        state.WatchSb.MemoryContext?.Inotify.Unregister(owner, state);
    }

    /// <summary>Deliver a filesystem event to every watch registered on <paramref name="inode" />.</summary>
    public static void Raise(Inode? inode, uint mask, string? name = null, uint cookie = 0, Inode? subject = null)
    {
        if (inode == null)
            return;

        if (name == null)
        {
            Raise(inode, mask, ReadOnlySpan<byte>.Empty, cookie, subject);
            return;
        }

        Raise(inode, mask, Encoding.UTF8.GetBytes(name), cookie, subject);
    }

    /// <summary>Deliver an event whose name is passed as raw filesystem bytes.</summary>
    public static void Raise(Inode? inode, uint mask, ReadOnlySpan<byte> name, uint cookie = 0,
        Inode? subject = null)
    {
        inode?.SuperBlock?.MemoryContext?.Inotify.Raise(inode, mask, name, cookie, subject);
    }

    private static uint _nextCookie;

    public static uint NextCookie()
    {
        // Cookies identify a rename pair and must not collide with an unrelated move;
        // forcing the low bit made consecutive values repeat (1, 3, 3, 5).
        var next = Interlocked.Increment(ref _nextCookie);
        if (next != 0)
            return next;

        // Wrapped through zero, which Linux reserves for "no cookie".
        return Interlocked.Increment(ref _nextCookie);
    }

    /// <summary>
    ///     Deliver an event about <paramref name="inode" /> to watches on it and (as Linux
    ///     does for child events) to watches on its parent directory, carrying the name.
    /// </summary>
    public static void RaiseChild(Dentry? dentry, Inode? inode, uint mask, bool? childUnlinked = null)
    {
        var unlinked = childUnlinked ?? dentry?.IsUnlinkedFromNamespace == true;
        inode?.SuperBlock?.MemoryContext?.Inotify.RaiseChild(dentry, inode, mask, unlinked);
    }
}
