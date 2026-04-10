using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class TbCoh
{
    private sealed class ScratchState
    {
        public readonly HashSet<HostPage> HostPages = [];
        public readonly Dictionary<VMAManager, HashSet<uint>> InvalidationPagesByMm = [];
        public readonly List<HashSet<uint>> InvalidationPageSetPool = [];
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

    private static void CollectInvalidations(VMAManager mm, uint guestPageStart, ref InvalidationScanState state)
    {
        if (GetCoherenceIdentity(mm) == state.WriterIdentity)
            return;

        var invByMm = state.InvalidationPagesByMm;
        if (!invByMm.TryGetValue(mm, out var pages))
        {
            pages = RentInvalidationPageSet(state.Scratch, ref state.UsedSetCount);
            invByMm[mm] = pages;
        }

        pages.Add(guestPageStart);
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

        var result = hostPage.ApplyTbCohPolicyIfChanged();
        TbCohDiagnosticsScope.Record(result);
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
        hostPage.VisitTbCohExecPages(ref invalidationState, CollectInvalidations);

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
