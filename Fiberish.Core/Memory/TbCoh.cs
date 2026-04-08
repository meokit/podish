using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class TbCoh
{
    private static nuint GetCoherenceIdentity(VMAManager mm)
    {
        return mm.AddressSpaceIdentity;
    }

    internal static void SyncWp(VMAManager mm, Engine engine)
    {
        var pages = mm.SnapshotTbWpPages();
        if (pages.Count == 0) return;

        Console.WriteLine($"[TbCoh] SyncWp mm={mm.GetHashCode():X} pages={pages.Count}");

        foreach (var pageStart in pages)
        {
            var vma = mm.FindVmArea(pageStart);
            if (vma == null || (vma.Perms & Protection.Write) == 0)
            {
                Console.WriteLine($"[TbCoh] SyncWp drop mm={mm.GetHashCode():X} page=0x{pageStart:X8}");
                mm.UnmarkTbWp(pageStart);
                continue;
            }

            if (!mm.ExternalPages.TryGet(pageStart, out _))
                continue;

            Console.WriteLine($"[TbCoh] SyncWp reprotect mm={mm.GetHashCode():X} page=0x{pageStart:X8} perms={vma.Perms}");
            engine.ReprotectMappedRange(pageStart, LinuxConstants.PageSize, (byte)(vma.Perms & ~Protection.Write));
        }
    }

    internal static void ApplyWxRange(VMAManager mm, uint addr, uint len)
    {
        if (len == 0) return;

        var hostPages = new HashSet<HostPage>();
        mm.CollectManagedHostPagesInRange(addr, len, hostPages);
        foreach (var hostPage in hostPages)
            ApplyWx(hostPage);
    }

    internal static void ApplyWx(HostPage hostPage)
    {
        ArgumentNullException.ThrowIfNull(hostPage);

        var hits = new List<RmapHit>();
        VmRmap.ResolveHostPageHolders(hostPage.Ptr, hits);
        if (hits.Count == 0) return;

        Console.WriteLine($"[TbCoh] ApplyWx host=0x{hostPage.Ptr.ToInt64():X} hits={hits.Count}");

        var execIdentities = new HashSet<nuint>();
        foreach (var hit in hits)
            if ((hit.Vma.Perms & Protection.Exec) != 0)
                execIdentities.Add(GetCoherenceIdentity(hit.Mm));

        var seenWriters = new HashSet<(VMAManager Mm, uint PageStart)>();
        foreach (var hit in hits)
        {
            if ((hit.Vma.Perms & Protection.Write) == 0)
                continue;

            var pageStart = hit.Vma.GetGuestPageStart(hit.PageIndex);
            if (!seenWriters.Add((hit.Mm, pageStart)))
                continue;

            var writerIdentity = GetCoherenceIdentity(hit.Mm);
            var hasCrossMmuExecPeer = false;
            foreach (var execIdentity in execIdentities)
                if (execIdentity != writerIdentity)
                {
                    hasCrossMmuExecPeer = true;
                    break;
                }
            if (hasCrossMmuExecPeer)
            {
                Console.WriteLine($"[TbCoh] ApplyWx mark-wp mm={hit.Mm.GetHashCode():X} page=0x{pageStart:X8}");
                hit.Mm.MarkTbWp(pageStart);
            }
            else
            {
                Console.WriteLine($"[TbCoh] ApplyWx clear-wp mm={hit.Mm.GetHashCode():X} page=0x{pageStart:X8}");
                hit.Mm.UnmarkTbWp(pageStart);
            }
        }
    }

    internal static void OnWriteFault(VMAManager writerMm, uint pageStart, HostPage hostPage)
    {
        ArgumentNullException.ThrowIfNull(writerMm);
        ArgumentNullException.ThrowIfNull(hostPage);

        var hits = new List<RmapHit>();
        VmRmap.ResolveHostPageHolders(hostPage.Ptr, hits);
        if (hits.Count == 0)
            return;

        var writerIdentity = GetCoherenceIdentity(writerMm);
        var invByMm = new Dictionary<VMAManager, HashSet<uint>>();
        foreach (var hit in hits)
        {
            // Same-MMU aliases are invalidated natively by host-page code-cache shootdown.
            if (GetCoherenceIdentity(hit.Mm) == writerIdentity)
                continue;
            if ((hit.Vma.Perms & Protection.Exec) == 0)
                continue;

            if (!invByMm.TryGetValue(hit.Mm, out var pages))
            {
                pages = [];
                invByMm[hit.Mm] = pages;
            }

            pages.Add(hit.Vma.GetGuestPageStart(hit.PageIndex));
        }

        if (invByMm.Count == 0)
            return;

        Console.WriteLine(
            $"[TbCoh] OnWriteFault writerMm={writerMm.GetHashCode():X} page=0x{pageStart:X8} host=0x{hostPage.Ptr.ToInt64():X} targets={invByMm.Count}");
        ApplyWx(hostPage);
        writerMm.MarkTbWp(pageStart);

        foreach (var (targetMm, pages) in invByMm)
        {
            var sequence = targetMm.BumpMapSequence();
            foreach (var guestPage in pages)
                targetMm.RecordCodeCacheResetRange(sequence, guestPage, LinuxConstants.PageSize);
        }
    }
}
