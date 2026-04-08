using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class TbCoh
{
    private sealed class ScratchState
    {
        public readonly HashSet<HostPage> HostPages = [];
        public readonly List<RmapHit> Hits = [];
        public readonly HashSet<nuint> ExecIdentities = [];
        public readonly HashSet<(VMAManager Mm, uint PageStart)> SeenWriters = [];
        public readonly Dictionary<VMAManager, HashSet<uint>> InvalidationPagesByMm = [];
        public readonly List<HashSet<uint>> InvalidationPageSetPool = [];
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

    private static bool HasCrossMmuExecPeer(HashSet<nuint> execIdentities, nuint writerIdentity)
    {
        foreach (var execIdentity in execIdentities)
            if (execIdentity != writerIdentity)
                return true;
        return false;
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

            if (!mm.ExternalPages.TryGet(pageStart, out _))
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
        var hits = scratch.Hits;
        var execIdentities = scratch.ExecIdentities;
        var seenWriters = scratch.SeenWriters;
        execIdentities.Clear();
        seenWriters.Clear();
        VmRmap.ResolveHostPageHolders(hostPage.Ptr, hits);
        if (hits.Count == 0) return;


        foreach (var hit in hits)
            if ((hit.Vma.Perms & Protection.Exec) != 0)
                execIdentities.Add(GetCoherenceIdentity(hit.Mm));

        foreach (var hit in hits)
        {
            if ((hit.Vma.Perms & Protection.Write) == 0)
                continue;

            var pageStart = hit.GuestPageStart;
            if (!seenWriters.Add((hit.Mm, pageStart)))
                continue;

            var writerIdentity = GetCoherenceIdentity(hit.Mm);
            if (HasCrossMmuExecPeer(execIdentities, writerIdentity))
            {
                hit.Mm.MarkTbWp(pageStart);
            }
            else
            {
                hit.Mm.UnmarkTbWp(pageStart);
            }
        }

        hits.Clear();
        execIdentities.Clear();
        seenWriters.Clear();
    }

    internal static void OnWriteFault(VMAManager writerMm, uint pageStart, HostPage hostPage)
    {
        ArgumentNullException.ThrowIfNull(writerMm);
        ArgumentNullException.ThrowIfNull(hostPage);

        var scratch = GetScratch();
        var hits = scratch.Hits;
        VmRmap.ResolveHostPageHolders(hostPage.Ptr, hits);
        if (hits.Count == 0)
            return;

        var writerIdentity = GetCoherenceIdentity(writerMm);
        var invByMm = scratch.InvalidationPagesByMm;
        foreach (var pages in invByMm.Values)
            pages.Clear();
        invByMm.Clear();
        var usedSetCount = 0;
        foreach (var hit in hits)
        {
            // Same-MMU aliases are invalidated natively by host-page code-cache shootdown.
            if (GetCoherenceIdentity(hit.Mm) == writerIdentity)
                continue;
            if ((hit.Vma.Perms & Protection.Exec) == 0)
                continue;

            if (!invByMm.TryGetValue(hit.Mm, out var pages))
            {
                pages = RentInvalidationPageSet(scratch, ref usedSetCount);
                invByMm[hit.Mm] = pages;
            }

            pages.Add(hit.GuestPageStart);
        }

        if (invByMm.Count == 0)
        {
            hits.Clear();
            return;
        }

        hits.Clear();
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
