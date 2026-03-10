using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

internal static class ProcessAddressSpaceSync
{
    private static readonly object AddressSpaceRegistryLock = new();
    private static readonly Dictionary<VMAManager, HashSet<Engine>> EnginesByAddressSpace = [];
    private static readonly Dictionary<IntPtr, (VMAManager AddressSpace, Engine Engine)> AddressSpaceByEngineState = [];

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

    private static Process? ResolveProcess(Engine engine, Process? process)
    {
        return process ?? (engine.Owner as FiberTask)?.Process;
    }

    internal static AddressSpaceScope EnterAddressSpaceScope(Engine engine, Process? process = null)
    {
        var ownerProcess = ResolveProcess(engine, process);
        return new AddressSpaceScope(ownerProcess);
    }

    internal static void RegisterEngineAddressSpace(VMAManager addressSpace, Engine engine)
    {
        var state = engine.State;
        if (state == IntPtr.Zero) return;

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
                    existing.Engine = engine;
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

    private static List<Engine> SnapshotAddressSpaceEngines(VMAManager addressSpace, Engine? includeEngine = null)
    {
        var engines = new List<Engine>();
        var seen = new HashSet<IntPtr>();

        if (includeEngine != null && includeEngine.State != IntPtr.Zero)
        {
            engines.Add(includeEngine);
            seen.Add(includeEngine.State);
        }

        lock (AddressSpaceRegistryLock)
        {
            if (EnginesByAddressSpace.TryGetValue(addressSpace, out var set))
            {
                foreach (var engine in set)
                {
                    var state = engine.State;
                    if (state == IntPtr.Zero) continue;
                    if (!seen.Add(state)) continue;
                    engines.Add(engine);
                }
            }
        }

        return engines;
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

    private static void SyncSharedMappingsForEngine(VMAManager vmaManager, Engine engine, uint addr, uint len)
    {
        SyncSharedMappingsForEngines(vmaManager, [engine], addr, len);
    }

    private static void SyncSharedMappingsForEngines(VMAManager vmaManager, IReadOnlyList<Engine> engines, uint addr,
        uint len)
    {
        if (len == 0) return;
        if (engines.Count == 0) return;
        var end = ComputeRangeEnd(addr, len);
        foreach (var vma in vmaManager.FindVMAsInRange(addr, end))
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            VMAManager.SyncVMA(vma, engines, addr, end);
        }
    }

    private static void UnmapPeerNativeMappings(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process, bool syncShared)
    {
        if (len == 0) return;
        var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
        foreach (var cpu in engines)
        {
            if (ReferenceEquals(cpu, engine)) continue;

            if (syncShared) SyncSharedMappingsForEngine(vmaManager, cpu, addr, len);
            cpu.InvalidateRange(addr, len);
            cpu.MemUnmap(addr, len);
        }
    }

    private static void MunmapCore(VMAManager vmaManager, Engine engine, uint addr, uint len, Process? process)
    {
        var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
        SyncSharedMappingsForEngines(vmaManager, engines, addr, len);
        vmaManager.Munmap(addr, len, engine);
        UnmapPeerNativeMappings(vmaManager, engine, addr, len, process, syncShared: false);
    }

    internal static void Munmap(VMAManager vmaManager, Engine engine, uint addr, uint len, Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        MunmapCore(vmaManager, engine, addr, len, process);
    }

    internal static int Mprotect(VMAManager vmaManager, Engine engine, uint addr, uint len, Protection prot,
        Process? process = null)
    {
        if (len == 0) return 0;
        using var scope = EnterAddressSpaceScope(engine, process);
        engine.InvalidateRange(addr, len);
        var rc = vmaManager.Mprotect(addr, len, prot, engine);
        if (rc == 0)
            UnmapPeerNativeMappings(vmaManager, engine, addr, len, process, syncShared: true);
        return rc;
    }

    internal static uint Mmap(VMAManager vmaManager, Engine engine, uint addr, uint len, Protection perms, MapFlags flags,
        LinuxFile? file, long offset, long filesz, string name, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var alignedLen = AlignLengthToPage(len);
        var fixedReplace = (flags & MapFlags.Fixed) != 0 && (flags & MapFlags.FixedNoReplace) == 0;
        if (fixedReplace && addr != 0 && alignedLen != 0)
            MunmapCore(vmaManager, engine, addr, alignedLen, process);

        return vmaManager.Mmap(addr, len, perms, flags, file, offset, filesz, name, engine);
    }

    internal static void SyncSharedRange(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process = null)
    {
        if (len == 0) return;
        using var scope = EnterAddressSpaceScope(engine, process);
        var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
        SyncSharedMappingsForEngines(vmaManager, engines, addr, len);
    }

    internal static void SyncMappedFile(VMAManager vmaManager, Engine engine, LinuxFile file, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var inode = file.OpenedInode;
        if (inode == null)
        {
            var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
            vmaManager.SyncMappedFile(file, engines);
            return;
        }

        var targets = inode.SnapshotMappedAddressSpaces();
        if (targets.Length == 0)
        {
            var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
            vmaManager.SyncMappedFile(file, engines.Count == 0 ? [engine] : engines);
            return;
        }

        foreach (var target in targets)
        {
            var fallback = ReferenceEquals(target, vmaManager) ? engine : null;
            var engines = SnapshotAddressSpaceEngines(target, fallback);
            if (engines.Count == 0)
            {
                target.SyncMappedFile(file, engine);
                continue;
            }

            target.SyncMappedFile(file, engines);
        }
    }

    internal static void NotifyInodeTruncated(VMAManager vmaManager, Engine engine, Inode inode, long newSize,
        Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var targets = inode.SnapshotMappedAddressSpaces();
        if (targets.Length == 0)
        {
            var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
            if (engines.Count == 0)
                vmaManager.OnFileTruncate(inode, newSize, engine);
            else
                foreach (var cpu in engines)
                    vmaManager.OnFileTruncate(inode, newSize, cpu);
            return;
        }

        foreach (var target in targets)
        {
            var fallback = ReferenceEquals(target, vmaManager) ? engine : null;
            var engines = SnapshotAddressSpaceEngines(target, fallback);
            if (engines.Count == 0)
            {
                target.OnFileTruncate(inode, newSize, engine);
                continue;
            }

            foreach (var cpu in engines)
                target.OnFileTruncate(inode, newSize, cpu);
        }
    }

    internal static void SyncAllMappedSharedFiles(VMAManager vmaManager, Engine engine, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var engines = SnapshotAddressSpaceEngines(vmaManager, engine);
        vmaManager.SyncAllMappedSharedFiles(engines);
    }
}
