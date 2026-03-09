using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

internal static class ProcessAddressSpaceSync
{
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

    private static List<Engine> SnapshotProcessEngines(Process process, Engine? includeEngine)
    {
        var engines = new List<Engine>();
        var seen = new HashSet<IntPtr>();

        if (includeEngine != null && includeEngine.State != IntPtr.Zero)
        {
            engines.Add(includeEngine);
            seen.Add(includeEngine.State);
        }

        lock (process.Threads)
        {
            foreach (var task in process.Threads)
            {
                var cpu = task.CPU;
                if (cpu.State == IntPtr.Zero) continue;
                if (!seen.Add(cpu.State)) continue;
                engines.Add(cpu);
            }
        }

        return engines;
    }

    private static void SyncSharedMappingsForEngine(VMAManager vmaManager, Engine engine, uint addr, uint len)
    {
        if (len == 0) return;
        var end = ComputeRangeEnd(addr, len);
        foreach (var vma in vmaManager.FindVMAsInRange(addr, end))
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            VMAManager.SyncVMA(vma, engine, addr, end);
        }
    }

    private static void UnmapPeerNativeMappings(VMAManager vmaManager, Engine engine, uint addr, uint len,
        Process? process, bool syncShared)
    {
        if (len == 0) return;
        var ownerProcess = ResolveProcess(engine, process);
        if (ownerProcess == null) return;

        var engines = SnapshotProcessEngines(ownerProcess, engine);
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
        vmaManager.Munmap(addr, len, engine);
        UnmapPeerNativeMappings(vmaManager, engine, addr, len, process, syncShared: true);
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
        var ownerProcess = ResolveProcess(engine, process);
        if (ownerProcess == null)
        {
            SyncSharedMappingsForEngine(vmaManager, engine, addr, len);
            return;
        }

        foreach (var cpu in SnapshotProcessEngines(ownerProcess, engine))
            SyncSharedMappingsForEngine(vmaManager, cpu, addr, len);
    }

    internal static void SyncMappedFile(VMAManager vmaManager, Engine engine, LinuxFile file, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var ownerProcess = ResolveProcess(engine, process);
        if (ownerProcess == null)
        {
            vmaManager.SyncMappedFile(file, engine);
            return;
        }

        foreach (var cpu in SnapshotProcessEngines(ownerProcess, engine))
            vmaManager.SyncMappedFile(file, cpu);
    }

    internal static void SyncAllMappedSharedFiles(VMAManager vmaManager, Engine engine, Process? process = null)
    {
        using var scope = EnterAddressSpaceScope(engine, process);
        var ownerProcess = ResolveProcess(engine, process);
        if (ownerProcess == null)
        {
            vmaManager.SyncAllMappedSharedFiles(engine);
            return;
        }

        foreach (var cpu in SnapshotProcessEngines(ownerProcess, engine))
            vmaManager.SyncAllMappedSharedFiles(cpu);
    }
}
