using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.VFS;

public readonly record struct RecordLockQueryResult(
    bool HasConflict,
    short Type,
    long Start,
    long Length,
    int Pid);

public sealed class FileLockState
{
    public const short F_RDLCK = 0;
    public const short F_WRLCK = 1;
    public const short F_UNLCK = 2;

    private static readonly Lock WaitGraphGate = new();
    private static readonly Dictionary<object, HashSet<object>> BlockedOnOwners = new(ReferenceEqualityComparer.Instance);

    private readonly Lock _gate = new();
    private readonly List<RecordLockEntry> _recordLocks = [];
    private readonly HashSet<LinuxFile> _flockShared = new(ReferenceEqualityComparer.Instance);
    private LinuxFile? _flockExclusive;
    private int _flockType; // 0 = none, 1 = SH, 2 = EX
    private AsyncWaitQueue? _flockWaitQueue;
    private KernelScheduler? _flockScheduler;
    private AsyncWaitQueue? _recordWaitQueue;
    private KernelScheduler? _recordScheduler;

    public async ValueTask<int> FlockAsync(LinuxFile linuxFile, FiberTask? task, int operation)
    {
        var nonBlock = (operation & LinuxConstants.LOCK_NB) != 0;
        var op = operation & ~LinuxConstants.LOCK_NB;

        if (op != LinuxConstants.LOCK_SH && op != LinuxConstants.LOCK_EX && op != LinuxConstants.LOCK_UN)
            return -(int)Errno.EINVAL;

        while (true)
        {
            AsyncWaitQueue? waitQueue = null;
            AsyncWaitQueue? signalQueue = null;
            KernelScheduler? signalScheduler = null;
            var returnImmediate = false;

            lock (_gate)
            {
                if (op == LinuxConstants.LOCK_UN)
                {
                    if (ReleaseFlockLocked(linuxFile))
                    {
                        signalQueue = _flockWaitQueue;
                        signalScheduler = _flockScheduler;
                    }
                    returnImmediate = true;
                }
                else
                {
                    var canAcquire = op == LinuxConstants.LOCK_SH
                        ? _flockExclusive == null || ReferenceEquals(_flockExclusive, linuxFile)
                        : _flockType == 0 ||
                          ReferenceEquals(_flockExclusive, linuxFile) ||
                          (_flockShared.Count == 1 && _flockShared.Contains(linuxFile) && _flockExclusive == null);

                    if (canAcquire)
                    {
                        var downgraded = ReferenceEquals(_flockExclusive, linuxFile) && op == LinuxConstants.LOCK_SH;
                        if (op == LinuxConstants.LOCK_SH)
                        {
                            if (ReferenceEquals(_flockExclusive, linuxFile))
                                _flockExclusive = null;
                            _flockShared.Add(linuxFile);
                            _flockType = 1;
                        }
                        else
                        {
                            _flockShared.Remove(linuxFile);
                            _flockExclusive = linuxFile;
                            _flockType = 2;
                        }

                        if (downgraded)
                        {
                            signalQueue = _flockWaitQueue;
                            signalScheduler = _flockScheduler;
                        }
                        returnImmediate = true;
                    }
                    else
                    {
                        if (nonBlock || task == null)
                            return -(int)Errno.EAGAIN;

                        _flockScheduler ??= task.CommonKernel;
                        _flockWaitQueue ??= new AsyncWaitQueue(_flockScheduler);
                        _flockWaitQueue.Reset();
                        waitQueue = _flockWaitQueue;
                    }
                }
            }

            if (returnImmediate)
            {
                SignalQueueSafe(signalQueue, signalScheduler);
                return 0;
            }

            if (await waitQueue!.WaitInterruptiblyAsync(task!) == AwaitResult.Interrupted)
                return -(int)Errno.ERESTARTSYS;
        }
    }

    public RecordLockQueryResult GetLock(object ownerKey, short type, long start, long end)
    {
        lock (_gate)
        {
            foreach (var entry in _recordLocks)
            {
                if (Conflicts(ownerKey, type, start, end, entry))
                {
                    var len = entry.End == long.MaxValue ? 0 : entry.End - entry.Start + 1;
                    return new RecordLockQueryResult(true, entry.Type, entry.Start, len, entry.Pid);
                }
            }

            return default;
        }
    }

    public async ValueTask<int> SetLockAsync(object ownerKey, int pid, short type, long start, long end,
        bool blocking, FiberTask? task)
    {
        while (true)
        {
            AsyncWaitQueue? waitQueue = null;
            AsyncWaitQueue? signalQueue = null;
            KernelScheduler? signalScheduler = null;
            List<object>? blockingOwners = null;
            var returnImmediate = false;

            lock (_gate)
            {
                if (type == F_UNLCK)
                {
                    if (RemoveOwnerRangeLocked(ownerKey, start, end))
                    {
                        signalQueue = _recordWaitQueue;
                        signalScheduler = _recordScheduler;
                    }
                    returnImmediate = true;
                }
                else
                {
                    for (var i = 0; i < _recordLocks.Count; i++)
                    {
                        var entry = _recordLocks[i];
                        if (!Conflicts(ownerKey, type, start, end, entry))
                            continue;

                        if (!blocking || task == null)
                            return -(int)Errno.EAGAIN;

                        blockingOwners ??= [];
                        if (!blockingOwners.Contains(entry.OwnerKey))
                            blockingOwners.Add(entry.OwnerKey);
                    }

                    if (blockingOwners == null)
                    {
                        if (PlaceOwnerLockLocked(ownerKey, pid, type, start, end))
                        {
                            signalQueue = _recordWaitQueue;
                            signalScheduler = _recordScheduler;
                        }
                        returnImmediate = true;
                    }
                    else
                    {
                        if (!TryRegisterWaitEdge(ownerKey, blockingOwners))
                            return -(int)Errno.EDEADLK;

                        _recordScheduler ??= task!.CommonKernel;
                        _recordWaitQueue ??= new AsyncWaitQueue(_recordScheduler);
                        _recordWaitQueue.Reset();
                        waitQueue = _recordWaitQueue;
                    }
                }
            }

            if (returnImmediate)
            {
                SignalQueueSafe(signalQueue, signalScheduler);
                return 0;
            }

            try
            {
                if (await waitQueue!.WaitInterruptiblyAsync(task!) == AwaitResult.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
            }
            finally
            {
                ClearWaitEdges(ownerKey);
            }
        }
    }

    public void ReleasePosixLocks(object ownerKey)
    {
        AsyncWaitQueue? signalQueue = null;
        KernelScheduler? signalScheduler = null;
        lock (_gate)
        {
            if (RemoveAllOwnerLocksLocked(ownerKey))
            {
                signalQueue = _recordWaitQueue;
                signalScheduler = _recordScheduler;
            }
        }
        SignalQueueSafe(signalQueue, signalScheduler);
    }

    public void ReleaseFileDescriptorLocks(LinuxFile file)
    {
        AsyncWaitQueue? flockQueue = null;
        KernelScheduler? flockScheduler = null;
        AsyncWaitQueue? recordQueue = null;
        KernelScheduler? recordScheduler = null;
        lock (_gate)
        {
            if (ReleaseFlockLocked(file))
            {
                flockQueue = _flockWaitQueue;
                flockScheduler = _flockScheduler;
            }
            if (RemoveAllOwnerLocksLocked(file))
            {
                recordQueue = _recordWaitQueue;
                recordScheduler = _recordScheduler;
            }
        }
        SignalQueueSafe(flockQueue, flockScheduler);
        SignalQueueSafe(recordQueue, recordScheduler);
    }

    private static void SignalQueueSafe(AsyncWaitQueue? queue, KernelScheduler? scheduler)
    {
        if (queue == null || scheduler == null)
            return;
        if (scheduler.IsSchedulerThread)
            queue.Signal();
        else
            scheduler.Schedule(queue.Signal);
    }

    private bool ReleaseFlockLocked(LinuxFile file)
    {
        var changed = false;
        if (ReferenceEquals(_flockExclusive, file))
        {
            _flockExclusive = null;
            changed = true;
        }

        if (_flockShared.Remove(file))
            changed = true;

        if (_flockExclusive == null && _flockShared.Count == 0)
            _flockType = 0;

        return changed;
    }

    private static bool Conflicts(object ownerKey, short type, long start, long end, in RecordLockEntry existing)
    {
        if (ReferenceEquals(ownerKey, existing.OwnerKey))
            return false;
        if (start > existing.End || existing.Start > end)
            return false;
        return type == F_WRLCK || existing.Type == F_WRLCK;
    }

    private bool RemoveAllOwnerLocksLocked(object ownerKey)
    {
        var removed = false;
        for (var i = _recordLocks.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_recordLocks[i].OwnerKey, ownerKey))
            {
                _recordLocks.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    private bool RemoveOwnerRangeLocked(object ownerKey, long start, long end)
    {
        var changed = false;
        for (var i = _recordLocks.Count - 1; i >= 0; i--)
        {
            var entry = _recordLocks[i];
            if (!ReferenceEquals(entry.OwnerKey, ownerKey))
                continue;
            if (start > entry.End || entry.Start > end)
                continue;

            changed = true;
            _recordLocks.RemoveAt(i);

            if (entry.Start < start)
                _recordLocks.Add(entry with { End = start - 1 });
            if (entry.End > end)
                _recordLocks.Add(entry with { Start = end + 1 });
        }

        return changed;
    }

    private bool PlaceOwnerLockLocked(object ownerKey, int pid, short type, long start, long end)
    {
        var downgraded = false;
        for (var i = _recordLocks.Count - 1; i >= 0; i--)
        {
            var entry = _recordLocks[i];
            if (!ReferenceEquals(entry.OwnerKey, ownerKey))
                continue;
            if (start > entry.End || entry.Start > end)
                continue;

            if (entry.Type == F_WRLCK && type == F_RDLCK)
                downgraded = true;

            _recordLocks.RemoveAt(i);
            if (entry.Start < start)
                _recordLocks.Add(entry with { End = start - 1 });
            if (entry.End > end)
                _recordLocks.Add(entry with { Start = end + 1 });
        }

        var mergedStart = start;
        var mergedEnd = end;
        for (var i = _recordLocks.Count - 1; i >= 0; i--)
        {
            var entry = _recordLocks[i];
            if (!ReferenceEquals(entry.OwnerKey, ownerKey) || entry.Type != type)
                continue;

            var touchesLeft = entry.End != long.MaxValue && entry.End + 1 == mergedStart;
            var touchesRight = mergedEnd != long.MaxValue && mergedEnd + 1 == entry.Start;
            if (!touchesLeft && !touchesRight)
                continue;

            mergedStart = Math.Min(mergedStart, entry.Start);
            mergedEnd = Math.Max(mergedEnd, entry.End);
            _recordLocks.RemoveAt(i);
        }

        _recordLocks.Add(new RecordLockEntry(ownerKey, pid, mergedStart, mergedEnd, type));
        return downgraded;
    }

    private static bool TryRegisterWaitEdge(object waiterOwner, List<object> blockers)
    {
        lock (WaitGraphGate)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            for (var i = 0; i < blockers.Count; i++)
            {
                var b = blockers[i];
                if (ReferenceEquals(b, waiterOwner))
                    return false;
                stack.Push(b);
            }

            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!visited.Add(cur))
                    continue;
                if (BlockedOnOwners.TryGetValue(cur, out var nextSet))
                {
                    foreach (var next in nextSet)
                    {
                        if (ReferenceEquals(next, waiterOwner))
                            return false;
                        if (!visited.Contains(next))
                            stack.Push(next);
                    }
                }
            }

            if (!BlockedOnOwners.TryGetValue(waiterOwner, out var set))
            {
                set = new HashSet<object>(ReferenceEqualityComparer.Instance);
                BlockedOnOwners[waiterOwner] = set;
            }
            else
            {
                set.Clear();
            }

            for (var i = 0; i < blockers.Count; i++)
                set.Add(blockers[i]);

            return true;
        }
    }

    private static void ClearWaitEdges(object waiterOwner)
    {
        lock (WaitGraphGate)
        {
            BlockedOnOwners.Remove(waiterOwner);
        }
    }

    private readonly record struct RecordLockEntry(object OwnerKey, int Pid, long Start, long End, short Type);
}
