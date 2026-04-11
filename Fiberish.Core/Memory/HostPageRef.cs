using System.Runtime.CompilerServices;

namespace Fiberish.Memory;

internal enum HostPageKind
{
    PageCache,
    Anon,
    Zero
}

internal enum HostPageOwnerKind
{
    AddressSpace,
    AnonVma
}

internal readonly record struct HostPageOwnerBinding
{
    public required HostPageOwnerKind OwnerKind { get; init; }
    public AddressSpace? Mapping { get; init; }
    public AnonVma? AnonVmaRoot { get; init; }
    public required uint PageIndex { get; init; }

    public void Validate(HostPageKind pageKind)
    {
        switch (pageKind)
        {
            case HostPageKind.PageCache:
                if (OwnerKind != HostPageOwnerKind.AddressSpace || Mapping == null || AnonVmaRoot != null)
                    throw new InvalidOperationException("Page-cache host pages must bind to an AddressSpace root.");
                break;
            case HostPageKind.Anon:
                if (OwnerKind != HostPageOwnerKind.AnonVma || AnonVmaRoot == null || Mapping != null)
                    throw new InvalidOperationException("Anonymous host pages must bind to an anon-vma root.");
                break;
            case HostPageKind.Zero:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pageKind), pageKind, null);
        }
    }

    public bool Matches(in HostPageOwnerBinding other)
    {
        return OwnerKind == other.OwnerKind &&
               ReferenceEquals(Mapping, other.Mapping) &&
               ReferenceEquals(AnonVmaRoot, other.AnonVmaRoot) &&
               PageIndex == other.PageIndex;
    }
}

internal sealed class RmapAttachment
{
    public required VMAManager Mm { get; init; }
    public required VmArea Vma { get; init; }
    public required uint StartPageIndex { get; init; }
    public required uint EndPageIndexExclusive { get; init; }

    public bool Covers(uint pageIndex)
    {
        return pageIndex >= StartPageIndex && pageIndex < EndPageIndexExclusive;
    }
}

internal enum TbCohWriterPolicyKind
{
    Uninitialized = 0,
    NoWriters,
    AllowAllWriters,
    ProtectAllWriters,
    ProtectAllExceptSingleExecIdentity
}

internal readonly record struct TbCohWriterPolicy(TbCohWriterPolicyKind Kind, nuint ExecIdentity = 0);

internal enum TbCohApplyKind
{
    FastNoWriters,
    FastSamePolicy,
    SlowScan
}

internal readonly record struct TbCohApplyResult(TbCohApplyKind Kind, int VisitedWriterPages);

internal struct HostPageTbCohSummary
{
    public int WriterRefCount;
    public int ExecIdentityCount;
    public nuint SingleExecIdentity;
    public Dictionary<nuint, int>? ExecIdentityRefCounts;
    public TbCohWriterPolicy AppliedPolicy;
    public bool AppliedPolicyValid;

    public readonly TbCohExecSummary GetExecSummary()
    {
        return new TbCohExecSummary(ExecIdentityCount != 0, ExecIdentityCount > 1, SingleExecIdentity);
    }

    public readonly TbCohWriterPolicy GetDesiredPolicy()
    {
        if (WriterRefCount == 0)
            return new TbCohWriterPolicy(TbCohWriterPolicyKind.NoWriters);

        if (ExecIdentityCount == 0)
            return new TbCohWriterPolicy(TbCohWriterPolicyKind.AllowAllWriters);

        if (ExecIdentityCount == 1)
            return new TbCohWriterPolicy(TbCohWriterPolicyKind.ProtectAllExceptSingleExecIdentity, SingleExecIdentity);

        return new TbCohWriterPolicy(TbCohWriterPolicyKind.ProtectAllWriters);
    }
}

internal sealed class TbCohWorkSet
{
    private readonly HashSet<IntPtr> _hostPages = [];

    public int Count => _hostPages.Count;

    public void Add(IntPtr hostPagePtr)
    {
        if (hostPagePtr == IntPtr.Zero)
            return;

        _hostPages.Add(hostPagePtr);
    }

    public void AddIfChanged(IntPtr hostPagePtr, bool changed)
    {
        if (!changed)
            return;

        Add(hostPagePtr);
    }

    public void Visit(Action<IntPtr> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var hostPagePtr in _hostPages)
            visitor(hostPagePtr);
    }
}

internal readonly record struct HostPageRmapKey(
    VMAManager Mm,
    VmArea Vma,
    HostPageOwnerKind OwnerKind,
    uint PageIndex)
{
    public bool Equals(HostPageRmapKey other)
    {
        return ReferenceEquals(Mm, other.Mm) &&
               ReferenceEquals(Vma, other.Vma) &&
               OwnerKind == other.OwnerKind &&
               PageIndex == other.PageIndex;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(Mm));
        hash.Add(RuntimeHelpers.GetHashCode(Vma));
        hash.Add((int)OwnerKind);
        hash.Add(PageIndex);
        return hash.ToHashCode();
    }
}

internal struct HostPageRmapRef
{
    public required VMAManager Mm { get; init; }
    public required VmArea Vma { get; init; }
    public required HostPageOwnerKind OwnerKind { get; init; }
    public required uint PageIndex { get; init; }
    public required uint GuestPageStart { get; set; }

    public readonly HostPageRmapKey GetKey()
    {
        return new HostPageRmapKey(Mm, Vma, OwnerKind, PageIndex);
    }
}

internal sealed class OwnerRmapTracker
{
    private sealed class OwnerRmapPageBucket
    {
        public List<HostPageRmapRef> Entries { get; } = [];
        public Dictionary<HostPageRmapKey, int> EntryIndices { get; } = [];

        public bool IsEmpty => Entries.Count == 0;
    }

    private sealed class OwnerRmapBucket
    {
        public Dictionary<IntPtr, OwnerRmapPageBucket> Pages { get; } = [];

        public bool IsEmpty => Pages.Count == 0;
    }

    private readonly Dictionary<uint, OwnerRmapBucket> _buckets = [];

    private OwnerRmapPageBucket GetOrAddPageBucket(uint pageIndex, IntPtr hostPagePtr)
    {
        if (!_buckets.TryGetValue(pageIndex, out var bucket))
        {
            bucket = new OwnerRmapBucket();
            _buckets.Add(pageIndex, bucket);
        }

        if (bucket.Pages.TryGetValue(hostPagePtr, out var existing))
            return existing;

        var created = new OwnerRmapPageBucket();
        bucket.Pages.Add(hostPagePtr, created);
        return created;
    }

    private void RemovePageBucketIfEmpty(uint pageIndex, IntPtr hostPagePtr, OwnerRmapPageBucket pageBucket)
    {
        if (!pageBucket.IsEmpty)
            return;

        if (!_buckets.TryGetValue(pageIndex, out var bucket))
            return;

        bucket.Pages.Remove(hostPagePtr);
        if (bucket.IsEmpty)
            _buckets.Remove(pageIndex);
    }

    public void AddOrUpdate(uint pageIndex, IntPtr hostPagePtr, HostPageRmapRef entry, out HostPageRmapRef previous,
        out bool existed)
    {
        var pageBucket = GetOrAddPageBucket(pageIndex, hostPagePtr);
        var key = entry.GetKey();
        if (pageBucket.EntryIndices.TryGetValue(key, out var existingIndex))
        {
            existed = true;
            previous = pageBucket.Entries[existingIndex];
            pageBucket.Entries[existingIndex] = entry;
            return;
        }

        existed = false;
        previous = default;
        pageBucket.EntryIndices.Add(key, pageBucket.Entries.Count);
        pageBucket.Entries.Add(entry);
    }

    public bool TryRemove(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key, out HostPageRmapRef removed)
    {
        if (!_buckets.TryGetValue(pageIndex, out var bucket) ||
            !bucket.Pages.TryGetValue(hostPagePtr, out var pageBucket) ||
            !pageBucket.EntryIndices.Remove(key, out var index))
        {
            removed = default;
            return false;
        }

        removed = pageBucket.Entries[index];
        var lastIndex = pageBucket.Entries.Count - 1;
        if (index != lastIndex)
        {
            var swapped = pageBucket.Entries[lastIndex];
            pageBucket.Entries[index] = swapped;
            pageBucket.EntryIndices[swapped.GetKey()] = index;
        }

        pageBucket.Entries.RemoveAt(lastIndex);
        RemovePageBucketIfEmpty(pageIndex, hostPagePtr, pageBucket);
        return true;
    }

    public bool Contains(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key)
    {
        return _buckets.TryGetValue(pageIndex, out var bucket) &&
               bucket.Pages.TryGetValue(hostPagePtr, out var pageBucket) &&
               pageBucket.EntryIndices.ContainsKey(key);
    }

    public bool TryRebind(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey oldKey, HostPageRmapRef newEntry,
        out HostPageRmapRef previous)
    {
        if (!_buckets.TryGetValue(pageIndex, out var bucket) ||
            !bucket.Pages.TryGetValue(hostPagePtr, out var pageBucket) ||
            !pageBucket.EntryIndices.TryGetValue(oldKey, out var index))
        {
            previous = default;
            return false;
        }

        previous = pageBucket.Entries[index];
        var newKey = newEntry.GetKey();
        if (!oldKey.Equals(newKey))
        {
            pageBucket.EntryIndices.Remove(oldKey);
            pageBucket.EntryIndices[newKey] = index;
        }

        pageBucket.Entries[index] = newEntry;
        return true;
    }

    public void CollectHits(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, List<RmapHit> output)
    {
        if (!_buckets.TryGetValue(pageIndex, out var bucket) ||
            !bucket.Pages.TryGetValue(hostPagePtr, out var pageBucket))
        {
            return;
        }

        foreach (var entry in pageBucket.Entries)
            output.Add(new RmapHit(hostPageRef, entry.Mm, entry.Vma, entry.OwnerKind, entry.PageIndex, entry.GuestPageStart));
    }

    public bool Visit<TState>(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        if (!_buckets.TryGetValue(pageIndex, out var bucket) ||
            !bucket.Pages.TryGetValue(hostPagePtr, out var pageBucket) ||
            pageBucket.Entries.Count == 0)
        {
            return false;
        }

        foreach (var entry in pageBucket.Entries)
            visitor(hostPageRef, entry, ref state);
        return true;
    }
}

internal readonly record struct RmapHit(
    HostPageRef HostPageRef,
    VMAManager Mm,
    VmArea Vma,
    HostPageOwnerKind OwnerKind,
    uint PageIndex,
    uint GuestPageStart);

internal delegate void HostPageRmapVisitor<TState>(HostPageRef hostPageRef, in HostPageRmapRef rmapRef, ref TState state);
internal delegate void TbCohMmPageVisitor<TState>(VMAManager mm, uint guestPageStart, ref TState state);

internal readonly record struct TbCohExecSummary(bool HasExecPeer, bool HasMultipleExecIdentities, nuint ExecIdentity);

internal struct HostPageData
{
    public IntPtr Ptr;
    public HostPageKind Kind;
    public bool Dirty;
    public bool Uptodate;
    public bool Writeback;
    public int MapCount;
    public int PinCount;
    public int OwnerResidentCount;
    public long LastAccessTimestamp;
    public bool HasOwnerRoot;
    public HostPageOwnerKind OwnerRootKind;
    public AddressSpace? OwnerAddressSpace;
    public AnonVma? OwnerAnonRoot;
    public uint OwnerPageIndex;
    public BackingPageHandle BackingHandle;
    public HostPageTbCohSummary TbCohSummary;
}

internal struct HostPageSlot
{
    public bool InUse;
    public uint Generation;
    public HostPageData Page;
}

internal sealed class HostPageTableState
{
    public readonly Lock Gate = new();
    public readonly Dictionary<IntPtr, int> SlotByPtr = [];
    public readonly Stack<int> FreeSlots = [];
    public HostPageSlot[] Slots = new HostPageSlot[16];
    public int SlotCount;

    public ref HostPageSlot GetSlotRef(int slot)
    {
        return ref Slots[slot];
    }

    public int RentSlot()
    {
        if (FreeSlots.TryPop(out var slot))
            return slot;

        if (SlotCount == Slots.Length)
            Array.Resize(ref Slots, Slots.Length * 2);

        return SlotCount++;
    }
}

internal struct HostPageRef
{
    private struct WriterPolicyApplyState
    {
        public TbCohWriterPolicy Policy;
        public int VisitedWriterPages;
    }

    private struct ExecPageVisitState<TState>
    {
        public required TbCohMmPageVisitor<TState> Visitor;
        public TState State;
    }

    internal readonly HostPageTableState? Owner;
    internal readonly int Slot;
    internal readonly uint Generation;

    internal HostPageRef(HostPageTableState owner, int slot, uint generation)
    {
        Owner = owner;
        Slot = slot;
        Generation = generation;
    }

    public IntPtr Ptr
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return IntPtr.Zero;

            return owner.GetSlotRef(slot).Page.Ptr;
        }
    }

    public HostPageKind Kind
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return default;

            return owner.GetSlotRef(slot).Page.Kind;
        }
    }

    public bool Dirty
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return false;

            return owner.GetSlotRef(slot).Page.Dirty;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.Dirty = value;
        }
    }

    public bool Uptodate
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return false;

            return owner.GetSlotRef(slot).Page.Uptodate;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.Uptodate = value;
        }
    }

    public bool Writeback
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return false;

            return owner.GetSlotRef(slot).Page.Writeback;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.Writeback = value;
        }
    }

    public int MapCount
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return 0;

            return owner.GetSlotRef(slot).Page.MapCount;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.MapCount = value;
        }
    }

    public int PinCount
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return 0;

            return owner.GetSlotRef(slot).Page.PinCount;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.PinCount = value;
        }
    }

    public int OwnerResidentCount
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return 0;

            return owner.GetSlotRef(slot).Page.OwnerResidentCount;
        }
    }

    public long LastAccessTimestamp
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return 0;

            return owner.GetSlotRef(slot).Page.LastAccessTimestamp;
        }
        set
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return;

            owner.GetSlotRef(slot).Page.LastAccessTimestamp = value;
        }
    }

    public void UpgradeKind(HostPageKind preferredKind)
    {
        if (!TryGetLiveSlot(out var owner, out var slot))
            return;

        ref var page = ref owner.GetSlotRef(slot).Page;
        if (page.Kind == preferredKind)
            return;

        if (page.Kind == HostPageKind.Zero || preferredKind == HostPageKind.Zero)
            throw new InvalidOperationException(
                $"Host page kind mismatch for {page.Ptr}: existing={page.Kind}, requested={preferredKind}.");

        // Keep the non-zero kind stable if callsites discover the page through
        // slightly different paths before owner-root binding finishes.
        page.Kind = preferredKind;
    }

    public bool BindOwnerRoot(HostPageOwnerBinding ownerBinding)
    {
        if (!TryGetLiveSlot(out var owner, out var slot))
            return false;

        lock (owner.Gate)
        {
            ref var slotRef = ref owner.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != Generation)
                return false;

            ref var page = ref slotRef.Page;
            if (page.Kind == HostPageKind.Zero)
                return true;

            ownerBinding.Validate(page.Kind);
            if (page.HasOwnerRoot)
            {
                var existing = GetOwnerBinding(ref page);
                if (!existing.Matches(ownerBinding))
                    throw new InvalidOperationException(
                        $"Host page {page.Ptr} is already bound to a different owner root.");
            }
            else
            {
                SetOwnerBinding(ref page, ownerBinding);
            }

            page.OwnerResidentCount++;
        }

        return true;
    }

    public bool UnbindOwnerRoot(HostPageOwnerBinding ownerBinding)
    {
        if (!TryGetLiveSlot(out var owner, out var slot))
            return false;

        lock (owner.Gate)
        {
            ref var slotRef = ref owner.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != Generation)
                return false;

            ref var page = ref slotRef.Page;
            if (page.Kind == HostPageKind.Zero || !page.HasOwnerRoot)
                return true;

            var existing = GetOwnerBinding(ref page);
            if (!existing.Matches(ownerBinding))
                throw new InvalidOperationException(
                    $"Host page {page.Ptr} attempted to unbind a non-owning owner root.");

            ownerBinding = existing;
        }

        if (TryGetLiveSlot(out owner, out slot))
        {
            lock (owner.Gate)
            {
                ref var slotRef = ref owner.GetSlotRef(slot);
                if (!slotRef.InUse || slotRef.Generation != Generation)
                    return false;

                ref var page = ref slotRef.Page;
                if (!page.HasOwnerRoot || !GetOwnerBinding(ref page).Matches(ownerBinding))
                    return false;

                if (page.OwnerResidentCount > 0)
                    page.OwnerResidentCount--;

                if (page.OwnerResidentCount == 0)
                    ClearOwnerBinding(ref page);
            }
        }

        HostPageManager.TryRemoveIfUnused(this);
        return true;
    }

    public bool HasOwnerRoot
    {
        get
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return false;

            return owner.GetSlotRef(slot).Page.HasOwnerRoot;
        }
    }

    private static bool HasExecRole(Protection perms)
    {
        return (perms & Protection.Exec) != 0;
    }

    private static bool HasWriteRole(Protection perms)
    {
        return (perms & Protection.Write) != 0;
    }

    private static nuint GetCoherenceIdentity(VMAManager mm)
    {
        return mm.AddressSpaceIdentity;
    }

    private static void ApplyWriterPolicy(VMAManager mm, uint pageStart, ref WriterPolicyApplyState state)
    {
        state.VisitedWriterPages++;
        switch (state.Policy.Kind)
        {
            case TbCohWriterPolicyKind.AllowAllWriters:
            case TbCohWriterPolicyKind.NoWriters:
                mm.UnmarkTbWp(pageStart);
                return;
            case TbCohWriterPolicyKind.ProtectAllWriters:
                mm.MarkTbWp(pageStart);
                return;
            case TbCohWriterPolicyKind.ProtectAllExceptSingleExecIdentity:
                if (mm.AddressSpaceIdentity == state.Policy.ExecIdentity)
                    mm.UnmarkTbWp(pageStart);
                else
                    mm.MarkTbWp(pageStart);
                return;
            default:
                throw new InvalidOperationException($"Unsupported TbCoh writer policy: {state.Policy.Kind}.");
        }
    }

    public TbCohApplyResult ApplyTbCohPolicyIfChanged()
    {
        while (true)
        {
            if (!TryGetLiveSlot(out var owner, out var slot))
                return new TbCohApplyResult(TbCohApplyKind.FastNoWriters, 0);

            TbCohWriterPolicy desired;
            lock (owner.Gate)
            {
                ref var slotRef = ref owner.GetSlotRef(slot);
                if (!slotRef.InUse || slotRef.Generation != Generation)
                    continue;

                ref var page = ref slotRef.Page;
                desired = page.TbCohSummary.GetDesiredPolicy();
                if (desired.Kind == TbCohWriterPolicyKind.NoWriters)
                {
                    page.TbCohSummary.AppliedPolicy = desired;
                    page.TbCohSummary.AppliedPolicyValid = true;
                    return new TbCohApplyResult(TbCohApplyKind.FastNoWriters, 0);
                }

                if (page.TbCohSummary.AppliedPolicyValid && page.TbCohSummary.AppliedPolicy == desired)
                    return new TbCohApplyResult(TbCohApplyKind.FastSamePolicy, 0);
            }

            var applyState = new WriterPolicyApplyState { Policy = desired };
            VisitRmapRefs(ref applyState, ApplyWriterPolicyForRmapRef);

            lock (owner.Gate)
            {
                ref var slotRef = ref owner.GetSlotRef(slot);
                if (!slotRef.InUse || slotRef.Generation != Generation)
                    continue;

                ref var page = ref slotRef.Page;
                if (page.TbCohSummary.GetDesiredPolicy() != desired)
                    continue;

                page.TbCohSummary.AppliedPolicy = desired;
                page.TbCohSummary.AppliedPolicyValid = true;
                return new TbCohApplyResult(TbCohApplyKind.SlowScan, applyState.VisitedWriterPages);
            }
        }
    }

    public bool AddOrUpdateRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex,
        uint guestPageStart)
    {
        if (!TryGetOwnerBinding(out var ownerBinding))
            return false;
        if (ownerBinding.OwnerKind != ownerKind || ownerBinding.PageIndex != pageIndex)
            return false;

        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        var entry = new HostPageRmapRef
        {
            Mm = mm,
            Vma = vma,
            OwnerKind = ownerKind,
            PageIndex = pageIndex,
            GuestPageStart = guestPageStart
        };

        AddOrUpdateOwnerRmap(ownerBinding, entry, out var previous, out var existed);
        if (existed)
            return previous.GuestPageStart != guestPageStart &&
                   TryUpdateTbCohSummary(mm, previous.Vma.Perms, vma.Perms, out var changedExisting)
                ? changedExisting
                : false;

        return TryUpdateTbCohSummary(mm, Protection.None, vma.Perms, out var changed) && changed;
    }

    public bool RemoveRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex)
    {
        if (!TryGetOwnerBinding(out var ownerBinding))
            return false;
        if (ownerBinding.OwnerKind != ownerKind || ownerBinding.PageIndex != pageIndex)
            return false;

        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        if (!RemoveOwnerRmap(ownerBinding, key, out var removedEntry))
            return false;

        return TryUpdateTbCohSummary(mm, removedEntry.Vma.Perms, Protection.None, out var changed) && changed;
    }

    public bool UpdateTbCohRolesForRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex,
        uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (!TryGetOwnerBinding(out var ownerBinding))
            return false;
        if (ownerBinding.OwnerKind != ownerKind || ownerBinding.PageIndex != pageIndex)
            return false;

        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        if (!ContainsOwnerRmap(ownerBinding, key))
            return false;

        return TryUpdateTbCohSummary(mm, oldPerms, newPerms, out var changed) && changed;
    }

    public bool RebindRmapRef(VMAManager mm, VmArea oldVma, VmArea newVma, HostPageOwnerKind ownerKind, uint pageIndex,
        uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        ArgumentNullException.ThrowIfNull(mm);
        ArgumentNullException.ThrowIfNull(oldVma);
        ArgumentNullException.ThrowIfNull(newVma);

        var oldKey = new HostPageRmapKey(mm, oldVma, ownerKind, pageIndex);
        if (!TryGetOwnerBinding(out var ownerBinding))
            return false;
        if (ownerBinding.OwnerKind != ownerKind || ownerBinding.PageIndex != pageIndex)
            return false;

        var newEntry = new HostPageRmapRef
        {
            Mm = mm,
            Vma = newVma,
            OwnerKind = ownerKind,
            PageIndex = pageIndex,
            GuestPageStart = guestPageStart
        };

        if (!RebindOwnerRmap(ownerBinding, oldKey, newEntry, out var previous))
            return false;

        if (previous.GuestPageStart != guestPageStart)
            return TryUpdateTbCohSummary(mm, oldPerms, newPerms, out var changedRebindGuest) && changedRebindGuest;

        return TryUpdateTbCohSummary(mm, oldPerms, newPerms, out var changedRebind) && changedRebind;
    }

    public TbCohExecSummary GetTbCohExecSummary()
    {
        if (!TryGetLiveSlot(out var owner, out var slot))
            return default;

        lock (owner.Gate)
        {
            ref var slotRef = ref owner.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != Generation)
                return default;

            return slotRef.Page.TbCohSummary.GetExecSummary();
        }
    }

    public bool VisitTbCohExecPages<TState>(ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var visitState = new ExecPageVisitState<TState> { Visitor = visitor, State = state };
        if (!TryGetOwnerBinding(out var ownerBinding))
            return false;

        var visited = VisitOwnerRmapRefs(ownerBinding, ref visitState, VisitExecRmapRef);
        state = visitState.State;
        return visited;
    }

    public void CollectRmapHits(List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!TryGetOwnerBinding(out var ownerBinding))
            return;

        CollectOwnerRmapHits(ownerBinding, output);
    }

    public void VisitRmapRefs<TState>(ref TState state, HostPageRmapVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!TryGetOwnerBinding(out var ownerBinding))
            return;

        VisitOwnerRmapRefs(ownerBinding, ref state, visitor);
    }

    internal int SlotIndexForDebug => Slot;

    internal uint HandleGenerationForDebug => Generation;

    internal HostPageOwnerKind? OwnerRootKindForDebug => TryGetOwnerBinding(out var binding) ? binding.OwnerKind : null;

    internal AddressSpace? OwnerAddressSpaceForDebug =>
        TryGetOwnerBinding(out var binding) && binding.OwnerKind == HostPageOwnerKind.AddressSpace
            ? binding.Mapping
            : null;

    internal AnonVma? OwnerAnonRootForDebug =>
        TryGetOwnerBinding(out var binding) && binding.OwnerKind == HostPageOwnerKind.AnonVma
            ? binding.AnonVmaRoot
            : null;

    internal uint OwnerPageIndexForDebug => TryGetOwnerBinding(out var binding) ? binding.PageIndex : 0;
    internal BackingPageHandleReleaseKind BackingReleaseKindForDebug =>
        TryGetLiveSlot(out var owner, out var slot) ? owner.GetSlotRef(slot).Page.BackingHandle.ReleaseKind : default;

    private bool TryGetLiveSlot(out HostPageTableState owner, out int slot)
    {
        owner = Owner!;
        slot = Slot;
        if (owner == null)
            return false;
        if ((uint)slot >= (uint)owner.SlotCount)
            return false;

        ref var slotRef = ref owner.GetSlotRef(slot);
        return slotRef.InUse && slotRef.Generation == Generation;
    }

    private bool TryGetOwnerBinding(out HostPageOwnerBinding binding)
    {
        if (!TryGetLiveSlot(out var owner, out var slot))
        {
            binding = default;
            return false;
        }

        ref var page = ref owner.GetSlotRef(slot).Page;
        if (!page.HasOwnerRoot)
        {
            binding = default;
            return false;
        }

        binding = GetOwnerBinding(ref page);
        return true;
    }

    private static HostPageOwnerBinding GetOwnerBinding(ref HostPageData page)
    {
        return new HostPageOwnerBinding
        {
            OwnerKind = page.OwnerRootKind,
            Mapping = page.OwnerAddressSpace,
            AnonVmaRoot = page.OwnerAnonRoot,
            PageIndex = page.OwnerPageIndex
        };
    }

    private static void SetOwnerBinding(ref HostPageData page, HostPageOwnerBinding binding)
    {
        page.HasOwnerRoot = true;
        page.OwnerRootKind = binding.OwnerKind;
        page.OwnerAddressSpace = binding.OwnerKind == HostPageOwnerKind.AddressSpace ? binding.Mapping : null;
        page.OwnerAnonRoot = binding.OwnerKind == HostPageOwnerKind.AnonVma ? binding.AnonVmaRoot : null;
        page.OwnerPageIndex = binding.PageIndex;
    }

    private static void ClearOwnerBinding(ref HostPageData page)
    {
        page.HasOwnerRoot = false;
        page.OwnerRootKind = default;
        page.OwnerAddressSpace = null;
        page.OwnerAnonRoot = null;
        page.OwnerPageIndex = 0;
    }

    private void AddOrUpdateOwnerRmap(HostPageOwnerBinding binding, HostPageRmapRef entry, out HostPageRmapRef previous,
        out bool existed)
    {
        var hostPagePtr = Ptr;
        switch (binding.OwnerKind)
        {
            case HostPageOwnerKind.AddressSpace:
                binding.Mapping!.AddOrUpdateOwnerRmap(binding.PageIndex, hostPagePtr, entry, out previous, out existed);
                return;
            case HostPageOwnerKind.AnonVma:
                binding.AnonVmaRoot!.AddOrUpdateOwnerRmap(binding.PageIndex, hostPagePtr, entry, out previous, out existed);
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private bool RemoveOwnerRmap(HostPageOwnerBinding binding, HostPageRmapKey key, out HostPageRmapRef removed)
    {
        var hostPagePtr = Ptr;
        return binding.OwnerKind switch
        {
            HostPageOwnerKind.AddressSpace => binding.Mapping!.RemoveOwnerRmap(binding.PageIndex, hostPagePtr, key,
                out removed),
            HostPageOwnerKind.AnonVma => binding.AnonVmaRoot!.RemoveOwnerRmap(binding.PageIndex, hostPagePtr, key,
                out removed),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private bool ContainsOwnerRmap(HostPageOwnerBinding binding, HostPageRmapKey key)
    {
        var hostPagePtr = Ptr;
        return binding.OwnerKind switch
        {
            HostPageOwnerKind.AddressSpace => binding.Mapping!.ContainsOwnerRmap(binding.PageIndex, hostPagePtr, key),
            HostPageOwnerKind.AnonVma => binding.AnonVmaRoot!.ContainsOwnerRmap(binding.PageIndex, hostPagePtr, key),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private bool RebindOwnerRmap(HostPageOwnerBinding binding, HostPageRmapKey oldKey, HostPageRmapRef newEntry,
        out HostPageRmapRef previous)
    {
        var hostPagePtr = Ptr;
        return binding.OwnerKind switch
        {
            HostPageOwnerKind.AddressSpace => binding.Mapping!.RebindOwnerRmap(binding.PageIndex, hostPagePtr, oldKey,
                newEntry, out previous),
            HostPageOwnerKind.AnonVma => binding.AnonVmaRoot!.RebindOwnerRmap(binding.PageIndex, hostPagePtr, oldKey,
                newEntry, out previous),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private void CollectOwnerRmapHits(HostPageOwnerBinding binding, List<RmapHit> output)
    {
        var hostPagePtr = Ptr;
        switch (binding.OwnerKind)
        {
            case HostPageOwnerKind.AddressSpace:
                binding.Mapping!.CollectOwnerRmapHits(binding.PageIndex, hostPagePtr, this, output);
                return;
            case HostPageOwnerKind.AnonVma:
                binding.AnonVmaRoot!.CollectOwnerRmapHits(binding.PageIndex, hostPagePtr, this, output);
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private bool VisitOwnerRmapRefs<TState>(HostPageOwnerBinding binding, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        var hostPagePtr = Ptr;
        return binding.OwnerKind switch
        {
            HostPageOwnerKind.AddressSpace => binding.Mapping!.VisitOwnerRmapRefs(binding.PageIndex, hostPagePtr, this,
                ref state, visitor),
            HostPageOwnerKind.AnonVma => binding.AnonVmaRoot!.VisitOwnerRmapRefs(binding.PageIndex, hostPagePtr, this,
                ref state, visitor),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static void ApplyWriterPolicyForRmapRef(HostPageRef hostPageRef, in HostPageRmapRef rmapRef,
        ref WriterPolicyApplyState state)
    {
        if (!HasWriteRole(rmapRef.Vma.Perms))
            return;

        ApplyWriterPolicy(rmapRef.Mm, rmapRef.GuestPageStart, ref state);
    }

    private static void VisitExecRmapRef<TState>(HostPageRef hostPageRef, in HostPageRmapRef rmapRef,
        ref ExecPageVisitState<TState> state)
    {
        if (!HasExecRole(rmapRef.Vma.Perms))
            return;

        state.Visitor(rmapRef.Mm, rmapRef.GuestPageStart, ref state.State);
    }

    private bool TryUpdateTbCohSummary(VMAManager mm, Protection oldPerms, Protection newPerms, out bool changed)
    {
        changed = false;
        if (!TryGetLiveSlot(out var owner, out var slot))
            return false;

        lock (owner.Gate)
        {
            ref var slotRef = ref owner.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != Generation)
                return false;

            changed = UpdateTbCohSummary(ref slotRef.Page.TbCohSummary, mm, oldPerms, newPerms);
            return true;
        }
    }

    private static bool UpdateTbCohSummary(ref HostPageTbCohSummary summary, VMAManager mm, Protection oldPerms,
        Protection newPerms)
    {
        var oldExec = HasExecRole(oldPerms);
        var newExec = HasExecRole(newPerms);
        var oldWrite = HasWriteRole(oldPerms);
        var newWrite = HasWriteRole(newPerms);
        if (oldExec == newExec && oldWrite == newWrite)
            return false;

        if (oldWrite != newWrite)
        {
            if (newWrite)
            {
                summary.WriterRefCount++;
            }
            else if (summary.WriterRefCount > 0)
            {
                summary.WriterRefCount--;
            }
        }

        if (oldExec != newExec)
        {
            var identity = GetCoherenceIdentity(mm);
            if (newExec)
                AddExecIdentity(ref summary, identity);
            else
                RemoveExecIdentity(ref summary, identity);
        }

        summary.AppliedPolicyValid = false;
        return true;
    }

    private static void AddExecIdentity(ref HostPageTbCohSummary summary, nuint identity)
    {
        summary.ExecIdentityRefCounts ??= [];
        if (summary.ExecIdentityRefCounts.TryGetValue(identity, out var existing))
        {
            summary.ExecIdentityRefCounts[identity] = existing + 1;
            return;
        }

        summary.ExecIdentityRefCounts[identity] = 1;
        summary.ExecIdentityCount++;
        summary.SingleExecIdentity = summary.ExecIdentityCount == 1 ? identity : 0;
    }

    private static void RemoveExecIdentity(ref HostPageTbCohSummary summary, nuint identity)
    {
        var counts = summary.ExecIdentityRefCounts;
        if (counts == null || !counts.TryGetValue(identity, out var existing))
            return;

        if (existing > 1)
        {
            counts[identity] = existing - 1;
            return;
        }

        counts.Remove(identity);
        if (counts.Count == 0)
        {
            summary.ExecIdentityRefCounts = null;
            summary.ExecIdentityCount = 0;
            summary.SingleExecIdentity = 0;
            return;
        }

        summary.ExecIdentityCount--;
        if (summary.ExecIdentityCount == 1)
        {
            foreach (var remainingIdentity in counts.Keys)
            {
                summary.SingleExecIdentity = remainingIdentity;
                return;
            }
        }

        summary.SingleExecIdentity = 0;
    }
}

internal static class HostPageManager
{
    private sealed class ScopeRestore : IDisposable
    {
        private readonly HostPageTableState? _previous;

        public ScopeRestore(HostPageTableState? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            ScopedState.Value = _previous;
        }
    }

    private static readonly AsyncLocal<HostPageTableState?> ScopedState = new();
    private static readonly HostPageTableState DefaultState = new();

    private static HostPageTableState CurrentState => ScopedState.Value ?? DefaultState;

    internal static IDisposable BeginIsolatedScope()
    {
        var previous = ScopedState.Value;
        ScopedState.Value = new HostPageTableState();
        return new ScopeRestore(previous);
    }

    internal static HostPageRef GetOrCreate(IntPtr ptr, HostPageKind preferredKind)
    {
        if (ptr == IntPtr.Zero)
            throw new ArgumentException("Host page pointer must be non-zero.", nameof(ptr));

        var state = CurrentState;
        lock (state.Gate)
        {
            if (state.SlotByPtr.TryGetValue(ptr, out var slot))
            {
                ref var existing = ref state.GetSlotRef(slot);
                var hostPage = new HostPageRef(state, slot, existing.Generation);
                hostPage.UpgradeKind(preferredKind);
                return hostPage;
            }

            slot = state.RentSlot();
            ref var slotRef = ref state.GetSlotRef(slot);
            unchecked
            {
                slotRef.Generation++;
                if (slotRef.Generation == 0)
                    slotRef.Generation = 1;
            }

            slotRef.InUse = true;
            slotRef.Page = new HostPageData
            {
                Ptr = ptr,
                Kind = preferredKind,
                Uptodate = true,
                LastAccessTimestamp = MonotonicTime.GetTimestamp()
            };
            state.SlotByPtr[ptr] = slot;
            return new HostPageRef(state, slot, slotRef.Generation);
        }
    }

    internal static HostPageRef CreateWithBacking(ref BackingPageHandle backingHandle, HostPageKind preferredKind)
    {
        if (!backingHandle.IsValid)
            throw new ArgumentException("Backing handle must be valid.", nameof(backingHandle));

        var ptr = backingHandle.Pointer;
        var state = CurrentState;
        lock (state.Gate)
        {
            if (state.SlotByPtr.TryGetValue(ptr, out var slot))
            {
                ref var existing = ref state.GetSlotRef(slot);
                var hostPage = new HostPageRef(state, slot, existing.Generation);
                hostPage.UpgradeKind(preferredKind);
                if (existing.Page.BackingHandle.IsValid)
                    throw new InvalidOperationException($"Host page 0x{ptr.ToInt64():X} already owns backing state.");

                existing.Page.BackingHandle = backingHandle;
                backingHandle = default;
                return hostPage;
            }

            slot = state.RentSlot();
            ref var slotRef = ref state.GetSlotRef(slot);
            unchecked
            {
                slotRef.Generation++;
                if (slotRef.Generation == 0)
                    slotRef.Generation = 1;
            }

            slotRef.InUse = true;
            slotRef.Page = new HostPageData
            {
                Ptr = ptr,
                Kind = preferredKind,
                Uptodate = true,
                LastAccessTimestamp = MonotonicTime.GetTimestamp(),
                BackingHandle = backingHandle
            };
            backingHandle = default;
            state.SlotByPtr[ptr] = slot;
            return new HostPageRef(state, slot, slotRef.Generation);
        }
    }

    internal static bool TryLookup(IntPtr ptr, out HostPageRef pageRef)
    {
        var state = CurrentState;
        lock (state.Gate)
        {
            if (!state.SlotByPtr.TryGetValue(ptr, out var slot))
            {
                pageRef = default;
                return false;
            }

            ref var slotRef = ref state.GetSlotRef(slot);
            if (!slotRef.InUse)
            {
                pageRef = default;
                return false;
            }

            pageRef = new HostPageRef(state, slot, slotRef.Generation);
            return true;
        }
    }

    internal static HostPageRef GetRequired(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            throw new InvalidOperationException($"HostPage metadata for 0x{ptr.ToInt64():X} is not registered.");

        return page;
    }

    internal static void TryRemoveIfUnused(HostPageRef pageRef)
    {
        BackingPageHandle backingHandle = default;
        if (pageRef.Kind == HostPageKind.Zero)
            return;
        if (pageRef.OwnerResidentCount > 0 || pageRef.MapCount > 0 || pageRef.PinCount > 0 || pageRef.HasOwnerRoot)
            return;
        if (!TryGetLiveSlot(pageRef, out var state, out var slot))
            return;

        lock (state.Gate)
        {
            ref var slotRef = ref state.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != pageRef.Generation)
                return;
            if (slotRef.Page.Kind == HostPageKind.Zero)
                return;
            if (slotRef.Page.OwnerResidentCount > 0 || slotRef.Page.MapCount > 0 || slotRef.Page.PinCount > 0)
                return;
            if (slotRef.Page.HasOwnerRoot)
                return;

            backingHandle = slotRef.Page.BackingHandle;
            slotRef.Page.BackingHandle = default;
            state.SlotByPtr.Remove(slotRef.Page.Ptr);
            slotRef.Page = default;
            slotRef.InUse = false;
            state.FreeSlots.Push(slot);
        }

        if (backingHandle.IsValid)
            BackingPageHandle.Release(ref backingHandle);
    }

    internal static void TryRemoveIfUnused(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return;

        TryRemoveIfUnused(page);
    }

    internal static TbCohApplyResult ApplyTbCohPolicyIfChanged(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return new TbCohApplyResult(TbCohApplyKind.FastNoWriters, 0);

        return page.ApplyTbCohPolicyIfChanged();
    }

    internal static bool VisitTbCohExecPages<TState>(IntPtr ptr, ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!TryLookup(ptr, out var page))
            return false;

        return page.VisitTbCohExecPages(ref state, visitor);
    }

    internal static bool BindOwnerRoot(IntPtr ptr, HostPageKind preferredKind, HostPageOwnerBinding ownerBinding)
    {
        return GetOrCreate(ptr, preferredKind).BindOwnerRoot(ownerBinding);
    }

    internal static bool UnbindOwnerRoot(IntPtr ptr, HostPageOwnerBinding ownerBinding)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.UnbindOwnerRoot(ownerBinding);
    }

    internal static bool AddOrUpdateRmapRef(IntPtr ptr, HostPageKind preferredKind, VMAManager mm, VmArea vma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart)
    {
        return GetOrCreate(ptr, preferredKind).AddOrUpdateRmapRef(mm, vma, ownerKind, pageIndex, guestPageStart);
    }

    internal static bool RemoveRmapRef(IntPtr ptr, VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind,
        uint pageIndex)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.RemoveRmapRef(mm, vma, ownerKind, pageIndex);
    }

    internal static bool UpdateTbCohRolesForRmapRef(IntPtr ptr, VMAManager mm, VmArea vma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.UpdateTbCohRolesForRmapRef(mm, vma, ownerKind, pageIndex, guestPageStart, oldPerms, newPerms);
    }

    internal static bool RebindRmapRef(IntPtr ptr, VMAManager mm, VmArea oldVma, VmArea newVma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.RebindRmapRef(mm, oldVma, newVma, ownerKind, pageIndex, guestPageStart, oldPerms, newPerms);
    }

    private static bool TryGetLiveSlot(HostPageRef pageRef, out HostPageTableState state, out int slot)
    {
        state = pageRef.Owner!;
        slot = pageRef.Slot;
        if (state == null)
            return false;
        if ((uint)slot >= (uint)state.SlotCount)
            return false;

        ref var slotRef = ref state.GetSlotRef(slot);
        return slotRef.InUse && slotRef.Generation == pageRef.Generation;
    }

}

internal static class VmRmap
{
    internal static void ResolveHostPageHolders(IntPtr ptr, List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (!HostPageManager.TryLookup(ptr, out var hostPage))
            return;
        hostPage.CollectRmapHits(output);
    }

    internal static bool VisitHostPageHolders<TState>(IntPtr ptr, ref TState state, HostPageRmapVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!HostPageManager.TryLookup(ptr, out var hostPage))
            return false;

        hostPage.VisitRmapRefs(ref state, visitor);
        return true;
    }
}
