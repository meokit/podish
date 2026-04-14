using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

internal static class ProcessAddressSpaceSync
{
    private static readonly Lock AddressSpaceRegistryLock = new();
    private static readonly Dictionary<VMAManager, HashSet<Engine>> EnginesByAddressSpace = [];
    private static readonly Dictionary<IntPtr, (VMAManager AddressSpace, Engine Engine)> AddressSpaceByEngineState = [];
    [ThreadStatic] private static Stack<EngineSnapshotBuffer>? SnapshotBufferPool;

    private static Process? ResolveProcess(Engine engine, Process? process)
    {
        return process ?? (engine.Owner as FiberTask)?.Process;
    }

    private static void AssertSchedulerThread(Engine engine, string caller)
    {
        if (engine.Owner is FiberTask task)
            task.CommonKernel.AssertSchedulerThread(caller);
    }

    internal static AddressSpaceScope EnterAddressSpaceScope(Engine engine, Process? process = null)
    {
        AssertSchedulerThread(engine, nameof(EnterAddressSpaceScope));
        var ownerProcess = ResolveProcess(engine, process);
        return new AddressSpaceScope(ownerProcess);
    }

    internal static void RegisterEngineAddressSpace(VMAManager addressSpace, Engine engine)
    {
        var state = engine.State;
        if (state == IntPtr.Zero) return;
        engine.AddressSpaceMapSequenceSeen = addressSpace.CurrentMapSequence;

        lock (AddressSpaceRegistryLock)
        {
            if (AddressSpaceByEngineState.TryGetValue(state, out var existing))
            {
                if (!ReferenceEquals(existing.AddressSpace, addressSpace))
                {
                    if (EnginesByAddressSpace.TryGetValue(existing.AddressSpace, out var oldSet))
                    {
                        oldSet.Remove(existing.Engine);
                        if (oldSet.Count == 0)
                            EnginesByAddressSpace.Remove(existing.AddressSpace);
                    }
                }
                else
                {
                    if (!ReferenceEquals(existing.Engine, engine) &&
                        EnginesByAddressSpace.TryGetValue(addressSpace, out var existingSet))
                    {
                        existingSet.Remove(existing.Engine);
                        if (existingSet.Count == 0)
                            EnginesByAddressSpace.Remove(addressSpace);
                    }
                }
            }

            if (!EnginesByAddressSpace.TryGetValue(addressSpace, out var set))
            {
                set = [];
                EnginesByAddressSpace[addressSpace] = set;
            }

            set.Add(engine);
            AddressSpaceByEngineState[state] = (addressSpace, engine);
        }
    }

    internal static void UnregisterEngineAddressSpace(VMAManager addressSpace, Engine engine)
    {
        var state = engine.State;
        if (state == IntPtr.Zero) return;

        UnregisterEngineState(state, addressSpace, engine);
    }

    internal static void UnregisterEngineState(IntPtr state, VMAManager? fallbackAddressSpace = null,
        Engine? fallbackEngine = null)
    {
        if (state == IntPtr.Zero) return;

        lock (AddressSpaceRegistryLock)
        {
            if (!AddressSpaceByEngineState.TryGetValue(state, out var existing))
            {
                if (fallbackAddressSpace != null && fallbackEngine != null)
                    fallbackAddressSpace.CaptureDirtySharedPages(fallbackEngine);

                if (fallbackAddressSpace != null && fallbackEngine != null &&
                    EnginesByAddressSpace.TryGetValue(fallbackAddressSpace, out var fallbackSet))
                {
                    fallbackSet.Remove(fallbackEngine);
                    if (fallbackSet.Count == 0)
                        EnginesByAddressSpace.Remove(fallbackAddressSpace);
                }

                return;
            }

            existing.AddressSpace.CaptureDirtySharedPages(existing.Engine);
            AddressSpaceByEngineState.Remove(state);
            if (EnginesByAddressSpace.TryGetValue(existing.AddressSpace, out var set))
            {
                set.Remove(existing.Engine);
                if (set.Count == 0)
                    EnginesByAddressSpace.Remove(existing.AddressSpace);
            }

            if (fallbackAddressSpace != null && fallbackEngine != null &&
                !ReferenceEquals(existing.AddressSpace, fallbackAddressSpace) &&
                EnginesByAddressSpace.TryGetValue(fallbackAddressSpace, out var additionalFallbackSet))
            {
                additionalFallbackSet.Remove(fallbackEngine);
                if (additionalFallbackSet.Count == 0)
                    EnginesByAddressSpace.Remove(fallbackAddressSpace);
            }
        }
    }

    internal static void RebindEngineAddressSpace(VMAManager oldAddressSpace, VMAManager newAddressSpace, Engine engine)
    {
        if (ReferenceEquals(oldAddressSpace, newAddressSpace)) return;
        UnregisterEngineAddressSpace(oldAddressSpace, engine);
        RegisterEngineAddressSpace(newAddressSpace, engine);
    }

    private static EngineSnapshotLease RentEngineSnapshot()
    {
        var pool = SnapshotBufferPool ??= new Stack<EngineSnapshotBuffer>();
        if (!pool.TryPop(out var buffer))
            buffer = new EngineSnapshotBuffer();
        return new EngineSnapshotLease(buffer);
    }

    private static void FillAddressSpaceEngineSnapshot(VMAManager addressSpace, List<Engine> engines,
        HashSet<IntPtr> seenStates, Engine? includeEngine = null)
    {
        if (includeEngine != null && includeEngine.State != IntPtr.Zero)
        {
            engines.Add(includeEngine);
            seenStates.Add(includeEngine.State);
        }

        lock (AddressSpaceRegistryLock)
        {
            if (EnginesByAddressSpace.TryGetValue(addressSpace, out var set))
                foreach (var engine in set)
                {
                    var state = engine.State;
                    if (state == IntPtr.Zero) continue;
                    if (!seenStates.Add(state)) continue;
                    engines.Add(engine);
                }
        }
    }

    private static long GetMinSeenSequence(VMAManager addressSpace)
    {
        var min = long.MaxValue;
        lock (AddressSpaceRegistryLock)
        {
            if (!EnginesByAddressSpace.TryGetValue(addressSpace, out var set))
                return long.MaxValue;
            foreach (var engine in set)
            {
                if (engine.State == IntPtr.Zero) continue;
                if (engine.AddressSpaceMapSequenceSeen < min)
                    min = engine.AddressSpaceMapSequenceSeen;
            }
        }

        return min;
    }

    private static uint AlignLengthToPage(uint len)
    {
        if (len == 0) return 0;
        if (len > uint.MaxValue - LinuxConstants.PageOffsetMask)
            return uint.MaxValue & LinuxConstants.PageMask;
        return (len + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
    }

    private static uint ComputeRangeEnd(uint addr, uint len)
    {
        var end = unchecked(addr + len);
        return end < addr ? uint.MaxValue : end;
    }

    private static bool HasExecRole(Protection perms)
    {
        return (perms & Protection.Exec) != 0;
    }

    private static bool HasSharedWriterRole(MapFlags flags, Protection perms)
    {
        return (flags & MapFlags.Shared) != 0 && (perms & Protection.Write) != 0;
    }

    private static bool ShouldApplyWxForMapping(MapFlags flags, Protection perms)
    {
        return HasExecRole(perms) || HasSharedWriterRole(flags, perms);
    }

    private static bool WouldMprotectAffectTbCoh(VMAManager vmaManager, uint addr, uint len, Protection newProt)
    {
        if (len == 0) return false;

        var end = ComputeRangeEnd(addr, len);
        var changed = false;
        vmaManager.VisitVmAreasInRange(addr, end, vma =>
        {
            if (changed)
                return;

            var overlapStart = Math.Max(vma.Start, addr);
            var overlapEnd = Math.Min(vma.End, end);
            if (overlapStart >= overlapEnd)
                return;

            if (HasExecRole(vma.Perms) != HasExecRole(newProt) ||
                HasSharedWriterRole(vma.Flags, vma.Perms) != HasSharedWriterRole(vma.Flags, newProt))
                changed = true;
        });
        return changed;
    }

    private static void SyncSharedMappingsForEngines(VMAManager vmaManager, IReadOnlyList<Engine> engines, uint addr,
        uint len)
    {
        if (len == 0) return;
        if (engines.Count == 0) return;
        var end = ComputeRangeEnd(addr, len);
        vmaManager.VisitVmAreasInRange(addr, end, vma =>
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) return;
            VMAManager.SyncVmArea(vma, engines, addr, end);
        });
    }

    private static void MunmapCore(VMAManager vmaManager, Engine engine, uint addr, uint len)
    {
        using var snapshot = RentEngineSnapshot();
        FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
        SyncSharedMappingsForEngines(vmaManager, snapshot.Engines, addr, len);
        vmaManager.Munmap(addr, len, engine);
        PublishInvalidation(vmaManager, engine, addr, len, true);
    }

    internal static void Munmap(VMAManager vmaManager, Engine engine, uint addr, uint len, Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        MunmapCore(vmaManager, engine, addr, len);
    }

    internal static int Mprotect(VMAManager vmaManager, Engine engine, uint addr, uint len, Protection prot,
        Process? process = null)
    {
        if (len == 0) return 0;
        using var scope = EnterAddressSpaceScope(engine, process);
        var shouldApplyWx = WouldMprotectAffectTbCoh(vmaManager, addr, len, prot);
        var tbCohWorkSet = shouldApplyWx ? new TbCohWorkSet() : null;
        using var snapshot = RentEngineSnapshot();
        FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
        SyncSharedMappingsForEngines(vmaManager, snapshot.Engines, addr, len);
        var rc = vmaManager.Mprotect(addr, len, prot, engine, out var resetCodeCacheRange, tbCohWorkSet);
        if (rc == 0)
        {
            tbCohWorkSet?.Visit(hostPagePtr => TbCoh.ApplyWx(vmaManager.MemoryContext, hostPagePtr));
            PublishInvalidation(vmaManager, engine, addr, len, resetCodeCacheRange);
        }
        return rc;
    }

    internal static int MadviseForkInheritance(VMAManager vmaManager, Engine engine, uint addr, uint len,
        bool dontFork, Process? process = null)
    {
        if (len == 0) return 0;
        using var scope = EnterAddressSpaceScope(engine, process);
        return vmaManager.MadviseForkInheritance(addr, len, dontFork);
    }

    internal static int MadviseDontNeed(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process = null)
    {
        if (len == 0) return 0;
        using var scope = EnterAddressSpaceScope(engine, process);
        using var snapshot = RentEngineSnapshot();
        FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
        SyncSharedMappingsForEngines(vmaManager, snapshot.Engines, addr, len);
        var rc = vmaManager.MadviseDontNeed(addr, len, engine, out var resetCodeCacheRange);
        if (rc == 0)
            PublishInvalidation(vmaManager, engine, addr, len, resetCodeCacheRange);
        return rc;
    }

    internal static void SyncEngineBeforeRun(VMAManager vmaManager, Engine engine, Process? process = null)
    {
        if (vmaManager == null) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        TbCoh.SyncWp(vmaManager, engine);
        if (engine.AddressSpaceMapSequenceSeen >= vmaManager.CurrentMapSequence) return;

        var ranges = engine.AddressSpaceInvalidationScratch;
        ranges.Clear();
        var currentSeq = vmaManager.CollectCodeCacheResetRangesSince(engine.AddressSpaceMapSequenceSeen, ranges);
        if (engine.AddressSpaceMapSequenceSeen >= currentSeq) return;

        // The engine already shares the same MMU core, so we only need per-engine cache shootdown.
        engine.FlushMmuTlbOnly();
        foreach (var range in ranges)
            engine.ResetCodeCacheByRange(range.Start, range.Length);
        engine.AddressSpaceMapSequenceSeen = currentSeq;

        var minSeen = GetMinSeenSequence(vmaManager);
        if (minSeen != long.MaxValue)
            vmaManager.PruneCodeCacheResetRanges(minSeen);
    }

    internal static uint Mmap(VMAManager vmaManager, Engine engine, uint addr, uint len, Protection perms,
        MapFlags flags,
        LinuxFile? file, long offset, string name, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var alignedLen = AlignLengthToPage(len);
        var shouldApplyWx = alignedLen != 0 && ShouldApplyWxForMapping(flags, perms);
        var tbCohWorkSet = shouldApplyWx ? new TbCohWorkSet() : null;
        var fixedReplace = (flags & MapFlags.Fixed) != 0 && (flags & MapFlags.FixedNoReplace) == 0;
        if (fixedReplace && addr != 0 && alignedLen != 0)
            MunmapCore(vmaManager, engine, addr, alignedLen);

        var mapped = vmaManager.Mmap(addr, len, perms, flags, file, offset, name, engine, tbCohWorkSet);
        tbCohWorkSet?.Visit(hostPagePtr => TbCoh.ApplyWx(vmaManager.MemoryContext, hostPagePtr));
        PublishInvalidation(vmaManager, engine, mapped, alignedLen, true);
        return mapped;
    }

    internal static void PublishMappingChange(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        PublishInvalidation(vmaManager, engine, addr, len, true);
    }

    internal static void SyncSharedRange(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        using var snapshot = RentEngineSnapshot();
        FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
        SyncSharedMappingsForEngines(vmaManager, snapshot.Engines, addr, len);
    }

    internal static void SyncMappedFile(VMAManager vmaManager, Engine engine, LinuxFile file, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var inode = file.OpenedInode;
        if (inode == null)
        {
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
            vmaManager.SyncMappedFile(file, snapshot.Engines);
            return;
        }

        var targets = inode.SnapshotMappedAddressSpaces();
        if (targets.Length == 0)
        {
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
            vmaManager.SyncMappedFile(file, snapshot.Engines.Count == 0 ? [engine] : snapshot.Engines);
            return;
        }

        foreach (var target in targets)
        {
            var fallback = ReferenceEquals(target, vmaManager) ? engine : null;
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(target, snapshot.Engines, snapshot.SeenStates, fallback);
            if (snapshot.Engines.Count == 0)
            {
                target.SyncMappedFile(file, Array.Empty<Engine>());
                continue;
            }

            target.SyncMappedFile(file, snapshot.Engines);
        }
    }

    internal static void NotifyFileContentChanged(VMAManager vmaManager, Engine engine, Inode inode, long start,
        long len, Process? process = null)
    {
        if (len <= 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        var targets = inode.SnapshotMappedAddressSpaces();
        foreach (var target in targets)
        {
            var fallback = ReferenceEquals(target, vmaManager) ? engine : null;
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(target, snapshot.Engines, snapshot.SeenStates, fallback);
            target.NotifyFileContentChanged(inode, start, len, snapshot.Engines.Count == 0 ? [] : snapshot.Engines);
        }
    }

    internal static void NotifyInodeTruncated(VMAManager vmaManager, Engine engine, Inode inode, long newSize,
        Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var targets = inode.SnapshotMappedAddressSpaces();
        if (targets.Length == 0)
        {
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
            if (snapshot.Engines.Count == 0) snapshot.Engines.Add(engine);
            vmaManager.OnFileTruncate(inode, newSize, snapshot.Engines);
            return;
        }

        foreach (var target in targets)
        {
            var fallback = ReferenceEquals(target, vmaManager) ? engine : null;
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(target, snapshot.Engines, snapshot.SeenStates, fallback);
            target.OnFileTruncate(inode, newSize, snapshot.Engines.Count == 0 ? [] : snapshot.Engines);
        }
    }

    internal static void UnmapMappingRange(Inode inode, long start, long len, bool evenCows)
    {
        if (len <= 0) return;
        var targets = inode.SnapshotMappedAddressSpaces();
        foreach (var target in targets)
        {
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(target, snapshot.Engines, snapshot.SeenStates);
            target.UnmapMappingRange(inode, start, len, evenCows, snapshot.Engines.Count == 0 ? [] : snapshot.Engines);
        }
    }

    internal static void MigrateOverlayMappings(OverlayInode inode, Inode newBackingInode)
    {
        var targets = inode.SnapshotMappedAddressSpaces();
        foreach (var target in targets)
        {
            using var snapshot = RentEngineSnapshot();
            FillAddressSpaceEngineSnapshot(target, snapshot.Engines, snapshot.SeenStates);
            target.MigrateOverlayMappings(inode, newBackingInode, snapshot.Engines.Count == 0 ? [] : snapshot.Engines);
        }
    }

    internal static void SyncAllMappedSharedFiles(VMAManager vmaManager, Engine engine, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        using var snapshot = RentEngineSnapshot();
        FillAddressSpaceEngineSnapshot(vmaManager, snapshot.Engines, snapshot.SeenStates, engine);
        vmaManager.SyncAllMappedSharedFiles(snapshot.Engines);
    }

    internal static void PublishProtectionChange(VMAManager vmaManager, Engine engine, uint addr, uint len,
        bool resetCodeCacheRange, Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        PublishInvalidation(vmaManager, engine, addr, len, resetCodeCacheRange);
    }

    private static void PublishInvalidation(VMAManager vmaManager, Engine engine, uint addr, uint len,
        bool resetCodeCacheRange)
    {
        if (len == 0) return;
        var sequence = vmaManager.BumpMapSequence();
        if (resetCodeCacheRange)
            vmaManager.RecordCodeCacheResetRange(sequence, addr, len);
        engine.AddressSpaceMapSequenceSeen = sequence;
    }

    private sealed class EngineSnapshotBuffer
    {
        public readonly List<Engine> Engines = [];
        public readonly HashSet<IntPtr> SeenStates = [];
    }

    private readonly struct EngineSnapshotLease : IDisposable
    {
        private readonly EngineSnapshotBuffer _buffer;

        public EngineSnapshotLease(EngineSnapshotBuffer buffer)
        {
            _buffer = buffer;
        }

        public List<Engine> Engines => _buffer.Engines;
        public HashSet<IntPtr> SeenStates => _buffer.SeenStates;

        public void Dispose()
        {
            _buffer.Engines.Clear();
            _buffer.SeenStates.Clear();
            (SnapshotBufferPool ??= new Stack<EngineSnapshotBuffer>()).Push(_buffer);
        }
    }

    internal readonly struct AddressSpaceScope : IDisposable
    {
        public AddressSpaceScope(Process? process)
        {
            _ = process;
        }

        public void Dispose()
        {
            // No-op for single-thread scheduler. Keep scope shape for future lock integration.
        }
    }
}
