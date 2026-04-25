using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    private readonly record struct OwnerRmapPageBucketKey(uint PageIndex, IntPtr HostPagePtr);

    private struct OwnerRmapPageBucket
    {
        private const int SmallEntryThreshold = 4;
        private int _count;
        private HostPageRmapRef _singleEntry;
        private HostPageRmapRef[]? _entries;
        private Dictionary<HostPageRmapKey, int>? _entryIndices;

        public bool IsEmpty => _count == 0;

        public void AddOrUpdate(HostPageRmapRef entry, out HostPageRmapRef previous, out bool existed)
        {
            var key = entry.GetKey();
            if (_count == 0)
            {
                _singleEntry = entry;
                _count = 1;
                previous = default;
                existed = false;
                return;
            }

            if (_count == 1)
            {
                if (_singleEntry.GetKey().Equals(key))
                {
                    previous = _singleEntry;
                    _singleEntry = entry;
                    existed = true;
                    return;
                }

                EnsureEntryCapacity(2);
                _entries![0] = _singleEntry;
                _entries[1] = entry;
                _singleEntry = default;
                _count = 2;
                previous = default;
                existed = false;
                return;
            }

            if (TryGetIndex(key, out var existingIndex))
            {
                previous = _entries![existingIndex];
                _entries[existingIndex] = entry;
                existed = true;
                return;
            }

            var insertIndex = _count;
            EnsureEntryCapacity(insertIndex + 1);
            _entries![insertIndex] = entry;
            _count = insertIndex + 1;
            previous = default;
            existed = false;

            if (_entryIndices != null)
            {
                _entryIndices.Add(key, insertIndex);
                return;
            }

            if (_count > SmallEntryThreshold)
                PromoteToIndexed();
        }

        public bool TryRemove(HostPageRmapKey key, out HostPageRmapRef removed)
        {
            if (_count == 0)
            {
                removed = default;
                return false;
            }

            if (_count == 1)
            {
                if (!_singleEntry.GetKey().Equals(key))
                {
                    removed = default;
                    return false;
                }

                removed = _singleEntry;
                Clear();
                return true;
            }

            int index;
            if (_entryIndices != null)
            {
                if (!_entryIndices.Remove(key, out index))
                {
                    removed = default;
                    return false;
                }
            }
            else if (!TryGetIndexLinear(key, out index))
            {
                removed = default;
                return false;
            }

            removed = _entries![index];
            RemoveAt(index);
            return true;
        }

        public bool Contains(HostPageRmapKey key)
        {
            return TryGetIndex(key, out _);
        }

        public bool TryRebind(HostPageRmapKey oldKey, HostPageRmapRef newEntry, out HostPageRmapRef previous)
        {
            if (_count == 0)
            {
                previous = default;
                return false;
            }

            var newKey = newEntry.GetKey();
            if (_count == 1)
            {
                if (!_singleEntry.GetKey().Equals(oldKey))
                {
                    previous = default;
                    return false;
                }

                previous = _singleEntry;
                _singleEntry = newEntry;
                return true;
            }

            int index;
            if (_entryIndices != null)
            {
                if (!_entryIndices.TryGetValue(oldKey, out index))
                {
                    previous = default;
                    return false;
                }

                if (!oldKey.Equals(newKey))
                {
                    _entryIndices.Remove(oldKey);
                    _entryIndices[newKey] = index;
                }
            }
            else if (!TryGetIndexLinear(oldKey, out index))
            {
                previous = default;
                return false;
            }

            previous = _entries![index];
            _entries[index] = newEntry;
            return true;
        }

        public void CollectHits(HostPageRef hostPageRef, List<RmapHit> output)
        {
            if (_count == 0)
                return;

            if (_count == 1)
            {
                output.Add(new RmapHit(hostPageRef, _singleEntry.Mm, _singleEntry.Vma, _singleEntry.OwnerKind,
                    _singleEntry.PageIndex, _singleEntry.GuestPageStart));
                return;
            }

            var entries = _entries!;
            for (var i = 0; i < _count; i++)
            {
                var entry = entries[i];
                output.Add(new RmapHit(hostPageRef, entry.Mm, entry.Vma, entry.OwnerKind, entry.PageIndex,
                    entry.GuestPageStart));
            }
        }

        public bool Visit<TState>(HostPageRef hostPageRef, ref TState state, HostPageRmapVisitor<TState> visitor)
        {
            if (_count == 0)
                return false;

            if (_count == 1)
            {
                visitor(hostPageRef, _singleEntry, ref state);
                return true;
            }

            var entries = _entries!;
            for (var i = 0; i < _count; i++)
                visitor(hostPageRef, entries[i], ref state);
            return true;
        }

        private bool TryGetIndex(HostPageRmapKey key, out int index)
        {
            if (_count == 0)
            {
                index = -1;
                return false;
            }

            if (_count == 1)
            {
                if (_singleEntry.GetKey().Equals(key))
                {
                    index = 0;
                    return true;
                }

                index = -1;
                return false;
            }

            if (_entryIndices != null)
                return _entryIndices.TryGetValue(key, out index);

            return TryGetIndexLinear(key, out index);
        }

        private bool TryGetIndexLinear(HostPageRmapKey key, out int index)
        {
            var entries = _entries!;
            for (var i = 0; i < _count; i++)
            {
                if (!entries[i].GetKey().Equals(key))
                    continue;

                index = i;
                return true;
            }

            index = -1;
            return false;
        }

        private void EnsureEntryCapacity(int requiredCount)
        {
            if (_entries == null)
            {
                _entries = new HostPageRmapRef[Math.Max(SmallEntryThreshold, requiredCount)];
                return;
            }

            if (_entries.Length >= requiredCount)
                return;

            Array.Resize(ref _entries, Math.Max(_entries.Length * 2, requiredCount));
        }

        private void PromoteToIndexed()
        {
            var entryIndices = new Dictionary<HostPageRmapKey, int>(_count);
            var entries = _entries!;
            for (var i = 0; i < _count; i++)
                entryIndices.Add(entries[i].GetKey(), i);

            _entryIndices = entryIndices;
        }

        private void RemoveAt(int index)
        {
            var entries = _entries!;
            var lastIndex = _count - 1;
            if (index != lastIndex)
            {
                var swapped = entries[lastIndex];
                entries[index] = swapped;
                if (_entryIndices != null)
                    _entryIndices[swapped.GetKey()] = index;
            }

            entries[lastIndex] = default;
            _count--;

            if (_count == 1)
            {
                _singleEntry = entries[0];
                entries[0] = default;
                _entries = null;
                _entryIndices = null;
                return;
            }

            if (_count <= SmallEntryThreshold)
                _entryIndices = null;
        }

        private void Clear()
        {
            _count = 0;
            _singleEntry = default;
            _entries = null;
            _entryIndices = null;
        }
    }

    private readonly Dictionary<OwnerRmapPageBucketKey, OwnerRmapPageBucket> _buckets = [];

    private ref OwnerRmapPageBucket GetOrAddPageBucketRef(uint pageIndex, IntPtr hostPagePtr)
    {
        var bucketKey = new OwnerRmapPageBucketKey(pageIndex, hostPagePtr);
        return ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, bucketKey, out _);
    }

    public void AddOrUpdate(uint pageIndex, IntPtr hostPagePtr, HostPageRmapRef entry, out HostPageRmapRef previous,
        out bool existed)
    {
        ref var pageBucket = ref GetOrAddPageBucketRef(pageIndex, hostPagePtr);
        pageBucket.AddOrUpdate(entry, out previous, out existed);
    }

    public bool TryRemove(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key, out HostPageRmapRef removed)
    {
        var bucketKey = new OwnerRmapPageBucketKey(pageIndex, hostPagePtr);
        ref var pageBucket = ref CollectionsMarshal.GetValueRefOrNullRef(_buckets, bucketKey);
        if (Unsafe.IsNullRef(ref pageBucket) || !pageBucket.TryRemove(key, out removed))
        {
            removed = default;
            return false;
        }

        if (pageBucket.IsEmpty)
            _buckets.Remove(bucketKey);
        return true;
    }

    public bool Contains(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key)
    {
        ref var pageBucket = ref CollectionsMarshal.GetValueRefOrNullRef(
            _buckets,
            new OwnerRmapPageBucketKey(pageIndex, hostPagePtr));
        return !Unsafe.IsNullRef(ref pageBucket) && pageBucket.Contains(key);
    }

    public bool TryRebind(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey oldKey, HostPageRmapRef newEntry,
        out HostPageRmapRef previous)
    {
        ref var pageBucket = ref CollectionsMarshal.GetValueRefOrNullRef(
            _buckets,
            new OwnerRmapPageBucketKey(pageIndex, hostPagePtr));
        if (Unsafe.IsNullRef(ref pageBucket) || !pageBucket.TryRebind(oldKey, newEntry, out previous))
        {
            previous = default;
            return false;
        }

        return true;
    }

    public void CollectHits(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, List<RmapHit> output)
    {
        ref var pageBucket = ref CollectionsMarshal.GetValueRefOrNullRef(
            _buckets,
            new OwnerRmapPageBucketKey(pageIndex, hostPagePtr));
        if (Unsafe.IsNullRef(ref pageBucket))
            return;

        pageBucket.CollectHits(hostPageRef, output);
    }

    public bool Visit<TState>(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        ref var pageBucket = ref CollectionsMarshal.GetValueRefOrNullRef(
            _buckets,
            new OwnerRmapPageBucketKey(pageIndex, hostPagePtr));
        if (Unsafe.IsNullRef(ref pageBucket) || pageBucket.IsEmpty)
        {
            return false;
        }

        return pageBucket.Visit(hostPageRef, ref state, visitor);
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

    public void EnsureAdditionalFreshSlotCapacity(int additionalFreshSlots)
    {
        if (additionalFreshSlots <= 0)
            return;

        var reusableSlots = FreeSlots.Count;
        var requiredFreshSlots = Math.Max(0, additionalFreshSlots - reusableSlots);
        if (requiredFreshSlots == 0)
            return;

        var requiredSlotCount = checked(SlotCount + requiredFreshSlots);
        if (requiredSlotCount <= Slots.Length)
            return;

        var newLength = Slots.Length;
        while (newLength < requiredSlotCount)
            newLength *= 2;

        Array.Resize(ref Slots, newLength);
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

        TryRemoveIfUnused();
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

    internal void TryRemoveIfUnused()
    {
        BackingPageHandle backingHandle = default;
        IntPtr removedPtr = IntPtr.Zero;
        HostPageKind removedKind = default;
        if (Kind == HostPageKind.Zero)
            return;
        if (OwnerResidentCount > 0 || MapCount > 0 || PinCount > 0 || HasOwnerRoot)
            return;
        if (!TryGetLiveSlot(out var state, out var slot))
            return;

        lock (state.Gate)
        {
            ref var slotRef = ref state.GetSlotRef(slot);
            if (!slotRef.InUse || slotRef.Generation != Generation)
                return;
            if (slotRef.Page.Kind == HostPageKind.Zero)
                return;
            if (slotRef.Page.OwnerResidentCount > 0 || slotRef.Page.MapCount > 0 || slotRef.Page.PinCount > 0)
                return;
            if (slotRef.Page.HasOwnerRoot)
                return;

            backingHandle = slotRef.Page.BackingHandle;
            removedPtr = slotRef.Page.Ptr;
            removedKind = slotRef.Page.Kind;
            slotRef.Page.BackingHandle = default;
            state.SlotByPtr.Remove(slotRef.Page.Ptr);
            slotRef.Page = default;
            slotRef.InUse = false;
            state.FreeSlots.Push(slot);
        }

        if (backingHandle.IsValid)
            BackingPageHandle.Release(ref backingHandle);
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

    private bool TryGetLiveSlot(out HostPageTableState owner, out int slot)
    {
        owner = Owner!;
        slot = Slot;
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

internal sealed class HostPageManager : IDisposable
{
    private readonly HostPageTableState _state = new();

    private static int GetBatchInsertCapacity(int currentCount, int additionalEntries)
    {
        if (additionalEntries <= 0)
            return currentCount;

        var minRequired = checked(currentCount + additionalEntries);
        var growth = Math.Max(additionalEntries, Math.Max(256, currentCount / 2));
        return Math.Max(minRequired, checked(currentCount + growth));
    }

    private static HostPageOwnerBinding GetOwnerBinding(ref HostPageData page)
    {
        return new HostPageOwnerBinding
        {
            OwnerKind = page.OwnerRootKind,
            Mapping = page.OwnerRootKind == HostPageOwnerKind.AddressSpace ? page.OwnerAddressSpace : null,
            AnonVmaRoot = page.OwnerRootKind == HostPageOwnerKind.AnonVma ? page.OwnerAnonRoot : null,
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

    private static HostPageRef GetOrCreateLocked(HostPageTableState state, IntPtr ptr, HostPageKind preferredKind,
        long accessTimestamp)
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
            LastAccessTimestamp = accessTimestamp
        };
        state.SlotByPtr[ptr] = slot;
        return new HostPageRef(state, slot, slotRef.Generation);
    }

    private static HostPageRef CreateWithBackingLocked(HostPageTableState state, ref BackingPageHandle backingHandle,
        HostPageKind preferredKind, long accessTimestamp)
    {
        if (!backingHandle.IsValid)
            throw new ArgumentException("Backing handle must be valid.", nameof(backingHandle));

        var ptr = backingHandle.Pointer;
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
            LastAccessTimestamp = accessTimestamp,
            BackingHandle = backingHandle
        };
        backingHandle = default;
        state.SlotByPtr[ptr] = slot;
        return new HostPageRef(state, slot, slotRef.Generation);
    }

    private static void BindOwnerRootLocked(HostPageTableState state, in HostPageRef hostPage,
        HostPageOwnerBinding ownerBinding)
    {
        ref var slotRef = ref state.GetSlotRef(hostPage.Slot);
        if (!slotRef.InUse || slotRef.Generation != hostPage.Generation)
            throw new InvalidOperationException("Host page ref became invalid during batch owner binding.");

        ref var page = ref slotRef.Page;
        if (page.Kind == HostPageKind.Zero)
            return;

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

    internal HostPageRefStatsSnapshot CaptureStats()
    {
        var state = _state;
        HostPageKindRefStatsAccumulator pageCache = default;
        HostPageKindRefStatsAccumulator anon = default;
        HostPageKindRefStatsAccumulator zero = default;
        lock (state.Gate)
        {
            for (var slot = 0; slot < state.SlotCount; slot++)
            {
                ref var slotRef = ref state.GetSlotRef(slot);
                if (!slotRef.InUse)
                    continue;

                ref var page = ref slotRef.Page;
                switch (page.Kind)
                {
                    case HostPageKind.PageCache:
                        pageCache.Include(page);
                        break;
                    case HostPageKind.Anon:
                        anon.Include(page);
                        break;
                    case HostPageKind.Zero:
                        zero.Include(page);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        return new HostPageRefStatsSnapshot(pageCache.ToSnapshot(), anon.ToSnapshot(), zero.ToSnapshot());
    }

    internal HostPageRef GetOrCreate(IntPtr ptr, HostPageKind preferredKind)
    {
        if (ptr == IntPtr.Zero)
            throw new ArgumentException("Host page pointer must be non-zero.", nameof(ptr));

        var state = _state;
        lock (state.Gate)
        {
            return GetOrCreateLocked(state, ptr, preferredKind, MonotonicTime.GetTimestamp());
        }
    }

    internal HostPageRef CreateWithBacking(ref BackingPageHandle backingHandle, HostPageKind preferredKind)
    {
        if (!backingHandle.IsValid)
            throw new ArgumentException("Backing handle must be valid.", nameof(backingHandle));

        var state = _state;
        lock (state.Gate)
        {
            return CreateWithBackingLocked(state, ref backingHandle, preferredKind, MonotonicTime.GetTimestamp());
        }
    }

    internal void CreateWithBackingsAndBindOwnerRoots(Span<BackingPageHandle> backingHandles,
        HostPageKind preferredKind, ReadOnlySpan<HostPageOwnerBinding> ownerBindings, long accessTimestamp,
        Span<HostPageRef> hostPages)
    {
        if (backingHandles.Length != ownerBindings.Length)
            throw new ArgumentException("Batch host page registration inputs must have matching lengths.");
        if (hostPages.Length < backingHandles.Length)
            throw new ArgumentException("Output span is smaller than input batch.", nameof(hostPages));

        var count = backingHandles.Length;
        if (count == 0)
            return;

        var state = _state;
        lock (state.Gate)
        {
            var additionalSlots = 0;
            for (var i = 0; i < count; i++)
            {
                if (!backingHandles[i].IsValid)
                    throw new ArgumentException("All backing handles in a batch must be valid.", nameof(backingHandles));

                if (!state.SlotByPtr.ContainsKey(backingHandles[i].Pointer))
                    additionalSlots++;
            }

            if (additionalSlots > 0)
            {
                state.SlotByPtr.EnsureCapacity(GetBatchInsertCapacity(state.SlotByPtr.Count, additionalSlots));
                state.EnsureAdditionalFreshSlotCapacity(additionalSlots);
            }

            for (var i = 0; i < count; i++)
            {
                var hostPage = CreateWithBackingLocked(state, ref backingHandles[i], preferredKind, accessTimestamp);
                BindOwnerRootLocked(state, hostPage, ownerBindings[i]);

                ref var slotRef = ref state.GetSlotRef(hostPage.Slot);
                slotRef.Page.LastAccessTimestamp = accessTimestamp;
                hostPages[i] = hostPage;
            }
        }
    }

    internal bool TryLookup(IntPtr ptr, out HostPageRef pageRef)
    {
        var state = _state;
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

    internal HostPageRef GetRequired(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            throw new InvalidOperationException($"HostPage metadata for 0x{ptr.ToInt64():X} is not registered.");

        return page;
    }

    internal void TryRemoveIfUnused(HostPageRef pageRef)
    {
        pageRef.TryRemoveIfUnused();
    }

    internal void TryRemoveIfUnused(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return;

        TryRemoveIfUnused(page);
    }

    internal TbCohApplyResult ApplyTbCohPolicyIfChanged(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return new TbCohApplyResult(TbCohApplyKind.FastNoWriters, 0);

        return page.ApplyTbCohPolicyIfChanged();
    }

    internal bool VisitTbCohExecPages<TState>(IntPtr ptr, ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!TryLookup(ptr, out var page))
            return false;

        return page.VisitTbCohExecPages(ref state, visitor);
    }

    internal bool BindOwnerRoot(IntPtr ptr, HostPageKind preferredKind, HostPageOwnerBinding ownerBinding)
    {
        return GetOrCreate(ptr, preferredKind).BindOwnerRoot(ownerBinding);
    }

    internal bool UnbindOwnerRoot(IntPtr ptr, HostPageOwnerBinding ownerBinding)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.UnbindOwnerRoot(ownerBinding);
    }

    internal bool AddOrUpdateRmapRef(IntPtr ptr, HostPageKind preferredKind, VMAManager mm, VmArea vma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart)
    {
        return GetOrCreate(ptr, preferredKind).AddOrUpdateRmapRef(mm, vma, ownerKind, pageIndex, guestPageStart);
    }

    internal bool RemoveRmapRef(IntPtr ptr, VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind,
        uint pageIndex)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.RemoveRmapRef(mm, vma, ownerKind, pageIndex);
    }

    internal bool UpdateTbCohRolesForRmapRef(IntPtr ptr, VMAManager mm, VmArea vma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.UpdateTbCohRolesForRmapRef(mm, vma, ownerKind, pageIndex, guestPageStart, oldPerms, newPerms);
    }

    internal bool RebindRmapRef(IntPtr ptr, VMAManager mm, VmArea oldVma, VmArea newVma,
        HostPageOwnerKind ownerKind, uint pageIndex, uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        return page.RebindRmapRef(mm, oldVma, newVma, ownerKind, pageIndex, guestPageStart, oldPerms, newPerms);
    }

    public void Dispose()
    {
        List<BackingPageHandle>? handles = null;
        lock (_state.Gate)
        {
            for (var slot = 0; slot < _state.SlotCount; slot++)
            {
                ref var slotRef = ref _state.GetSlotRef(slot);
                if (!slotRef.InUse)
                    continue;

                if (slotRef.Page.BackingHandle.IsValid)
                {
                    handles ??= [];
                    handles.Add(slotRef.Page.BackingHandle);
                    slotRef.Page.BackingHandle = default;
                }

                slotRef.Page = default;
                slotRef.InUse = false;
            }

            _state.SlotByPtr.Clear();
            _state.FreeSlots.Clear();
            _state.SlotCount = 0;
        }

        if (handles == null)
            return;

        foreach (var handle in handles)
        {
            var released = handle;
            BackingPageHandle.Release(ref released);
        }
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
    internal static void ResolveHostPageHolders(HostPageManager hostPages, IntPtr ptr, List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(hostPages);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (!hostPages.TryLookup(ptr, out var hostPage))
            return;
        hostPage.CollectRmapHits(output);
    }

    internal static bool VisitHostPageHolders<TState>(HostPageManager hostPages, IntPtr ptr, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(hostPages);
        ArgumentNullException.ThrowIfNull(visitor);
        if (!hostPages.TryLookup(ptr, out var hostPage))
            return false;

        hostPage.VisitRmapRefs(ref state, visitor);
        return true;
    }
}
