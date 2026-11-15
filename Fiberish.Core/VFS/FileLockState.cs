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
    private readonly List<LockWaiter> _flockWaiters = [];
    private readonly List<LockWaiter> _recordWaiters = [];

    public async ValueTask<int> FlockAsync(LinuxFile linuxFile, FiberTask? task, int operation)
    {
        var nonBlock = (operation & LinuxConstants.LOCK_NB) != 0;
        var op = operation & ~LinuxConstants.LOCK_NB;

        if (op != LinuxConstants.LOCK_SH && op != LinuxConstants.LOCK_EX && op != LinuxConstants.LOCK_UN)
            return -(int)Errno.EINVAL;

        while (true)
        {
            LockWaiter? waiter = null;
            LockWaiter[]? toWake = null;
            var returnImmediate = false;

            lock (_gate)
            {
                if (op == LinuxConstants.LOCK_UN)
                {
                    if (ReleaseFlockLocked(linuxFile))
                    {
                        toWake = [.. _flockWaiters];
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
                            toWake = [.. _flockWaiters];
                        }
                        returnImmediate = true;
                    }
                    else
                    {
                        if (nonBlock || task == null)
                            return -(int)Errno.EAGAIN;

                        waiter = new LockWaiter(task.CommonKernel);
                        _flockWaiters.Add(waiter);
                    }
                }
            }

            if (returnImmediate)
            {
                SignalWaiters(toWake);
                return 0;
            }

            try
            {
                if (await waiter!.Queue.WaitInterruptiblyAsync(task!) == AwaitResult.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
            }
            finally
            {
                lock (_gate) _flockWaiters.Remove(waiter!);
            }
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
            LockWaiter? waiter = null;
            LockWaiter[]? toWake = null;
            List<object>? blockingOwners = null;
            var returnImmediate = false;

            lock (_gate)
            {
                if (type == F_UNLCK)
                {
                    if (RemoveOwnerRangeLocked(ownerKey, start, end))
                    {
                        toWake = [.. _recordWaiters];
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
                            toWake = [.. _recordWaiters];
                        }
                        returnImmediate = true;
                    }
                    else
                    {
                        if (!TryRegisterWaitEdge(ownerKey, blockingOwners))
                            return -(int)Errno.EDEADLK;

                        waiter = new LockWaiter(task!.CommonKernel);
                        _recordWaiters.Add(waiter);
                    }
                }
            }

            if (returnImmediate)
            {
                SignalWaiters(toWake);
                return 0;
            }

            try
            {
                if (await waiter!.Queue.WaitInterruptiblyAsync(task!) == AwaitResult.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
            }
            finally
            {
                lock (_gate) _recordWaiters.Remove(waiter!);
                ClearWaitEdges(ownerKey);
            }
        }
    }

    public void ReleasePosixLocks(object ownerKey)
    {
        LockWaiter[]? toWake = null;
        lock (_gate)
        {
            if (RemoveAllOwnerLocksLocked(ownerKey))
            {
                toWake = [.. _recordWaiters];
            }
        }
        SignalWaiters(toWake);
    }

    public void ReleaseFileDescriptorLocks(LinuxFile file)
    {
        LockWaiter[]? flockWaiters = null;
        LockWaiter[]? recordWaiters = null;
        lock (_gate)
        {
            if (ReleaseFlockLocked(file))
            {
                flockWaiters = [.. _flockWaiters];
            }
            if (RemoveAllOwnerLocksLocked(file))
            {
                recordWaiters = [.. _recordWaiters];
            }
        }
        SignalWaiters(flockWaiters);
        SignalWaiters(recordWaiters);
    }

    private static void SignalWaiters(LockWaiter[]? waiters)
    {
        if (waiters == null) return;
        foreach (var waiter in waiters)
            if (waiter.Scheduler.IsSchedulerThread)
                waiter.Queue.Signal();
            else
                waiter.Scheduler.Schedule(waiter.Queue.Signal);
    }

    private sealed class LockWaiter(KernelScheduler scheduler)
    {
        public KernelScheduler Scheduler { get; } = scheduler;
        public AsyncWaitQueue Queue { get; } = new(scheduler);
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
