using System.Buffers;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

/// <summary>
///     Inotify state belonging to one guest runtime (memory context): per-user quotas,
///     the per-inode watch registry and its fast-path counter. Keeping this off static
///     host fields prevents unrelated containers from sharing limits or locks.
/// </summary>
public sealed class InotifyRuntimeState
{
    private readonly Lock _limitsGate = new();
    private readonly Dictionary<int, int> _instances = [];
    private readonly Dictionary<int, int> _watches = [];
    private readonly Lock _registryGate = new();
    private readonly Dictionary<(SuperBlock Sb, ulong Ino), Dictionary<InotifyInode, int>> _registry = [];
    private int _activeTargetCount;
    private readonly Lock _finalizationGate = new();
    private readonly Dictionary<Inode, FinalizationState> _deferredFinalizations = new(ReferenceEqualityComparer.Instance);

    public int MaxQueuedEvents { get; set; } = 16384;
    public int MaxUserInstances { get; set; } = 128;
    public int MaxUserWatches { get; set; } = 65536;

    internal bool TryAcquireInstance(int uid)
    {
        lock (_limitsGate)
        {
            _instances.TryGetValue(uid, out var count);
            if (count >= MaxUserInstances) return false;
            _instances[uid] = count + 1;
            return true;
        }
    }

    internal void ReleaseInstance(int uid)
    {
        lock (_limitsGate)
            Decrement(_instances, uid, 1);
    }

    internal bool TryAcquireWatch(int uid)
    {
        lock (_limitsGate)
        {
            _watches.TryGetValue(uid, out var count);
            if (count >= MaxUserWatches) return false;
            _watches[uid] = count + 1;
            return true;
        }
    }

    internal void ReleaseWatch(int uid)
    {
        lock (_limitsGate)
            Decrement(_watches, uid, 1);
    }

    internal void ReleaseWatches(int uid, int count)
    {
        if (count <= 0) return;
        lock (_limitsGate)
            Decrement(_watches, uid, count);
    }

    internal void Register(InotifyInode owner, InotifyInode.WatchState state)
    {
        if (state.WatchSb == null || state.WatchIno == 0)
            return;

        lock (_registryGate)
        {
            var key = (state.WatchSb, state.WatchIno);
            if (!_registry.TryGetValue(key, out var owners))
                _registry[key] = owners = [];
            if (owners.TryAdd(owner, state.WatchDescriptor))
                _activeTargetCount++;
        }
    }

    internal void Unregister(InotifyInode owner, InotifyInode.WatchState state)
    {
        if (state.WatchSb == null || state.WatchIno == 0)
            return;

        lock (_registryGate)
        {
            var key = (state.WatchSb, state.WatchIno);
            if (!_registry.TryGetValue(key, out var owners))
                return;
            if (owners.Remove(owner))
                _activeTargetCount--;
            if (owners.Count == 0)
                _registry.Remove(key);
        }
    }

    internal void Raise(Inode? inode, uint mask, ReadOnlySpan<byte> name, uint cookie = 0, Inode? subject = null)
    {
        if (inode == null || Volatile.Read(ref _activeTargetCount) == 0)
            return;

        var count = SnapshotTargets(inode, out var buffer);
        if (count == 0)
            return;

        try
        {
            DeliverTargets(buffer, count, AdjustSelfMask(inode, mask, name), name, cookie, subject ?? inode,
                false);
        }
        finally
        {
            ArrayPool<Target>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal void RaiseChild(Dentry? dentry, Inode? inode, uint mask, bool childUnlinked)
    {
        if (inode == null)
            return;

        var isDir = inode.Type == InodeType.Directory;

        // Linux notifies the parent watch before the object's own watch. Only touch the
        // name when the parent actually has a watch: this runs on every read/write/close.
        var parent = dentry?.Parent?.Inode;
        if (parent != null && !ReferenceEquals(parent, inode))
        {
            var parentRuntime = parent.SuperBlock?.MemoryContext?.Inotify;
            if (parentRuntime != null)
                parentRuntime.RaiseTo(parent, isDir ? mask | LinuxConstants.IN_ISDIR : mask,
                    dentry!.Name.Bytes, 0, inode, childUnlinked);
        }

        RaiseTo(inode, AdjustSelfMask(inode, mask, ReadOnlySpan<byte>.Empty), ReadOnlySpan<byte>.Empty, 0, inode,
            false);
    }

    private void RaiseTo(Inode inode, uint mask, ReadOnlySpan<byte> name, uint cookie, Inode subject,
        bool subjectUnlinked)
    {
        if (Volatile.Read(ref _activeTargetCount) == 0)
            return;

        var count = SnapshotTargets(inode, out var buffer);
        if (count == 0)
            return;

        try
        {
            DeliverTargets(buffer, count, mask, name, cookie, subject, subjectUnlinked);
        }
        finally
        {
            ArrayPool<Target>.Shared.Return(buffer, clearArray: true);
        }
    }

    private int SnapshotTargets(Inode inode, out Target[] buffer)
    {
        buffer = [];
        if (inode.SuperBlock == null || inode.Ino == 0)
            return 0;

        lock (_registryGate)
        {
            if (!_registry.TryGetValue((inode.SuperBlock, inode.Ino), out var owners) || owners.Count == 0)
                return 0;

            buffer = ArrayPool<Target>.Shared.Rent(owners.Count);
            var index = 0;
            foreach (var pair in owners)
                buffer[index++] = new Target(pair.Key, pair.Value);
            return owners.Count;
        }
    }

    private static uint AdjustSelfMask(Inode inode, uint mask, ReadOnlySpan<byte> name)
    {
        return inode.Type == InodeType.Directory && name.IsEmpty &&
               (mask & (LinuxConstants.IN_ATTRIB | LinuxConstants.IN_ACCESS | LinuxConstants.IN_OPEN |
                        LinuxConstants.IN_CLOSE_WRITE | LinuxConstants.IN_CLOSE_NOWRITE)) != 0
            ? mask | LinuxConstants.IN_ISDIR
            : mask;
    }

    private static void DeliverTargets(Target[] targets, int count, uint mask, ReadOnlySpan<byte> name,
        uint cookie, Inode subject, bool subjectUnlinked)
    {
        for (var i = 0; i < count; i++)
            targets[i].Owner.DeliverEvent(targets[i].Wd, mask, name, cookie, subject, subjectUnlinked);
    }

    /// <summary>
    ///     Defer DELETE_SELF/IN_IGNORED for this inode until its mutation's other events
    ///     have been sent. Unrelated inodes are never suppressed, even in the same runtime.
    /// </summary>
    internal FinalizationSuspendScope SuspendFinalization(Inode inode)
    {
        // Unlink/rmdir/rename run on the scheduler thread; without any watch there is
        // nothing to defer, so keep the common no-inotify path allocation- and lock-free.
        if (Volatile.Read(ref _activeTargetCount) == 0)
            return default;

        lock (_finalizationGate)
        {
            _deferredFinalizations.TryGetValue(inode, out var state);
            _deferredFinalizations[inode] = state with { Depth = state.Depth + 1 };
        }
        return new FinalizationSuspendScope(this, inode);
    }

    internal void NotifyInodeFinalized(Inode inode)
    {
        if (Volatile.Read(ref _activeTargetCount) == 0)
            return;

        lock (_finalizationGate)
        {
            if (_deferredFinalizations.TryGetValue(inode, out var state))
            {
                _deferredFinalizations[inode] = state with { Pending = true };
                return;
            }
        }

        // The inode lost its last link and its last runtime reference. Direct watches
        // now receive DELETE_SELF followed by IN_IGNORED, exactly once.
        Raise(inode, LinuxConstants.IN_DELETE_SELF, ReadOnlySpan<byte>.Empty, 0, inode);
    }

    private void ResumeFinalization(Inode inode)
    {
        bool notify;
        lock (_finalizationGate)
        {
            if (!_deferredFinalizations.TryGetValue(inode, out var state))
                return;
            if (state.Depth > 1)
            {
                _deferredFinalizations[inode] = state with { Depth = state.Depth - 1 };
                return;
            }
            _deferredFinalizations.Remove(inode);
            notify = state.Pending;
        }
        // Delivery can invoke callbacks; do not hold the finalization lock across it.
        if (notify)
            NotifyInodeFinalized(inode);
    }

    private readonly record struct FinalizationState(int Depth, bool Pending);

    internal readonly struct FinalizationSuspendScope : IDisposable
    {
        private readonly InotifyRuntimeState _owner;
        private readonly Inode _inode;

        public FinalizationSuspendScope(InotifyRuntimeState owner, Inode inode)
        {
            _owner = owner;
            _inode = inode;
        }

        public void Dispose()
        {
            if (_owner == null) return;
            _owner.ResumeFinalization(_inode);
        }
    }

    internal void Clear()
    {
        lock (_finalizationGate)
            _deferredFinalizations.Clear();
        lock (_limitsGate)
        {
            _instances.Clear();
            _watches.Clear();
        }

        lock (_registryGate)
        {
            _registry.Clear();
            _activeTargetCount = 0;
        }
    }

    private static void Decrement(Dictionary<int, int> map, int uid, int count)
    {
        if (!map.TryGetValue(uid, out var current)) return;
        var next = current - count;
        if (next > 0) map[uid] = next;
        else map.Remove(uid);
    }

    private readonly record struct Target(InotifyInode Owner, int Wd);
}

/// <summary>Per-guest-network sysctl values (currently /proc/sys/net/ipv4/conf/*/tag).</summary>
public sealed class NetworkSysctlState
{
    private int _lo;
    private int _default;
    private int _all;

    public int GetTag(ProcSysNetworkInterface iface)
    {
        return iface switch
        {
            ProcSysNetworkInterface.Lo => Volatile.Read(ref _lo),
            ProcSysNetworkInterface.Default => Volatile.Read(ref _default),
            ProcSysNetworkInterface.All => Volatile.Read(ref _all),
            _ => 0
        };
    }

    public void SetTag(ProcSysNetworkInterface iface, int value)
    {
        switch (iface)
        {
            case ProcSysNetworkInterface.Lo:
                Volatile.Write(ref _lo, value);
                break;
            case ProcSysNetworkInterface.Default:
                Volatile.Write(ref _default, value);
                break;
            case ProcSysNetworkInterface.All:
                Volatile.Write(ref _all, value);
                break;
        }
    }
}

public enum ProcSysNetworkInterface
{
    Lo,
    Default,
    All
}
