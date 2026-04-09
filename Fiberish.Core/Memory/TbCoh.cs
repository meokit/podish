using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class TbCoh
{
    private sealed class ScratchState
    {
        public readonly HashSet<HostPage> HostPages = [];
        public readonly HashSet<(VMAManager Mm, uint PageStart)> SeenWriters = [];
        public readonly Dictionary<VMAManager, HashSet<uint>> InvalidationPagesByMm = [];
        public readonly List<HashSet<uint>> InvalidationPageSetPool = [];
    }

    private struct ExecPeerScanState
    {
        public bool HasExecPeer;
        public bool HasMultipleExecIdentities;
        public nuint ExecIdentity;
    }

    private struct WriterApplyState
    {
        public required HashSet<(VMAManager Mm, uint PageStart)> SeenWriters;
        public bool HasExecPeer;
        public bool HasMultipleExecIdentities;
        public nuint ExecIdentity;
    }

    private struct InvalidationScanState
    {
        public required Dictionary<VMAManager, HashSet<uint>> InvalidationPagesByMm;
        public required ScratchState Scratch;
        public nuint WriterIdentity;
        public int UsedSetCount;
    }

    [ThreadStatic] private static ScratchState? _scratch;

    private static nuint GetCoherenceIdentity(VMAManager mm)
    {
        return mm.AddressSpaceIdentity;
    }

    private static ScratchState GetScratch()
    {
        return _scratch ??= new ScratchState();
    }

    private static HashSet<uint> RentInvalidationPageSet(ScratchState scratch, ref int usedSetCount)
    {
        if (usedSetCount < scratch.InvalidationPageSetPool.Count)
        {
            var existing = scratch.InvalidationPageSetPool[usedSetCount++];
            existing.Clear();
            return existing;
        }

        var created = new HashSet<uint>();
        scratch.InvalidationPageSetPool.Add(created);
        usedSetCount++;
        return created;
    }

    private static void CollectExecPeer(HostPage _, in HostPageRmapRef rmapRef, ref ExecPeerScanState state)
    {
        if ((rmapRef.Vma.Perms & Protection.Exec) == 0)
            return;

        var hitIdentity = GetCoherenceIdentity(rmapRef.Mm);
        if (!state.HasExecPeer)
        {
            state.HasExecPeer = true;
            state.ExecIdentity = hitIdentity;
            return;
        }

        if (state.ExecIdentity != hitIdentity)
            state.HasMultipleExecIdentities = true;
    }

    private static void ApplyWriterProtection(HostPage _, in HostPageRmapRef rmapRef, ref WriterApplyState state)
    {
        if ((rmapRef.Vma.Perms & Protection.Write) == 0)
            return;

        var pageStart = rmapRef.GuestPageStart;
        if (!state.SeenWriters.Add((rmapRef.Mm, pageStart)))
            return;

        var writerIdentity = GetCoherenceIdentity(rmapRef.Mm);
        var shouldProtectWriter =
            state.HasMultipleExecIdentities || (state.HasExecPeer && state.ExecIdentity != writerIdentity);
        if (shouldProtectWriter)
            rmapRef.Mm.MarkTbWp(pageStart);
        else
            rmapRef.Mm.UnmarkTbWp(pageStart);
    }

    private static void CollectInvalidations(HostPage _, in HostPageRmapRef rmapRef, ref InvalidationScanState state)
    {
        if ((rmapRef.Vma.Perms & Protection.Exec) == 0)
            return;
        if (GetCoherenceIdentity(rmapRef.Mm) == state.WriterIdentity)
            return;

        var invByMm = state.InvalidationPagesByMm;
        if (!invByMm.TryGetValue(rmapRef.Mm, out var pages))
        {
            pages = RentInvalidationPageSet(state.Scratch, ref state.UsedSetCount);
            invByMm[rmapRef.Mm] = pages;
        }

        pages.Add(rmapRef.GuestPageStart);
    }

    internal static void SyncWp(VMAManager mm, Engine engine)
    {
        var pages = mm.SnapshotTbWpPages();
        if (pages.Count == 0) return;


        foreach (var pageStart in pages)
        {
            var vma = mm.FindVmArea(pageStart);
            if (vma == null || (vma.Perms & Protection.Write) == 0)
            {
                mm.UnmarkTbWp(pageStart);
                continue;
            }

            if (!mm.Pages.TryGet(pageStart, out _))
                continue;

            engine.ReprotectMappedRange(pageStart, LinuxConstants.PageSize, (byte)(vma.Perms & ~Protection.Write));
        }
    }

    internal static void ApplyWxRange(VMAManager mm, uint addr, uint len)
    {
        if (len == 0) return;

        var scratch = GetScratch();
        var hostPages = scratch.HostPages;
        hostPages.Clear();
        mm.CollectManagedHostPagesInRange(addr, len, hostPages);
        foreach (var hostPage in hostPages)
            ApplyWx(hostPage);
        hostPages.Clear();
    }

    internal static void ApplyWx(HostPage hostPage)
    {
        ArgumentNullException.ThrowIfNull(hostPage);

        var scratch = GetScratch();
        var seenWriters = scratch.SeenWriters;
        seenWriters.Clear();
        var execState = new ExecPeerScanState();
        if (!VmRmap.VisitHostPageHolders(hostPage.Ptr, ref execState, CollectExecPeer))
            return;

        var writerState = new WriterApplyState
        {
            SeenWriters = seenWriters,
            HasExecPeer = execState.HasExecPeer,
            HasMultipleExecIdentities = execState.HasMultipleExecIdentities,
            ExecIdentity = execState.ExecIdentity
        };
        VmRmap.VisitHostPageHolders(hostPage.Ptr, ref writerState, ApplyWriterProtection);
        seenWriters.Clear();
    }

    internal static void OnWriteFault(VMAManager writerMm, uint pageStart, HostPage hostPage)
    {
        ArgumentNullException.ThrowIfNull(writerMm);
        ArgumentNullException.ThrowIfNull(hostPage);

        var scratch = GetScratch();
        var invByMm = scratch.InvalidationPagesByMm;
        foreach (var pages in invByMm.Values)
            pages.Clear();
        invByMm.Clear();
        var invalidationState = new InvalidationScanState
        {
            InvalidationPagesByMm = invByMm,
            Scratch = scratch,
            WriterIdentity = GetCoherenceIdentity(writerMm),
            UsedSetCount = 0
        };
        if (!VmRmap.VisitHostPageHolders(hostPage.Ptr, ref invalidationState, CollectInvalidations))
            return;

        if (invByMm.Count == 0)
            return;

        ApplyWx(hostPage);
        writerMm.MarkTbWp(pageStart);

        foreach (var (targetMm, pages) in invByMm)
        {
            var sequence = targetMm.BumpMapSequence();
            foreach (var guestPage in pages)
                targetMm.RecordCodeCacheResetRange(sequence, guestPage, LinuxConstants.PageSize);
        }

        foreach (var pages in invByMm.Values)
            pages.Clear();
        invByMm.Clear();
    }
}
