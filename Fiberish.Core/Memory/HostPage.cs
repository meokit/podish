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

internal sealed class HostPageOwnerRef
{
    public required HostPageOwnerKind OwnerKind { get; init; }
    public AddressSpace? Mapping { get; init; }
    public AnonVma? AnonVma { get; init; }
    public required uint PageIndex { get; init; }

    public bool Matches(HostPageOwnerRef other)
    {
        return OwnerKind == other.OwnerKind &&
               ReferenceEquals(Mapping, other.Mapping) &&
               ReferenceEquals(AnonVma, other.AnonVma) &&
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

internal readonly record struct TbCohMmPageKey(VMAManager Mm, uint GuestPageStart)
{
    public bool Equals(TbCohMmPageKey other)
    {
        return ReferenceEquals(Mm, other.Mm) &&
               GuestPageStart == other.GuestPageStart;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(Mm));
        hash.Add(GuestPageStart);
        return hash.ToHashCode();
    }
}

internal sealed class TbCohMmPageEntry
{
    public required VMAManager Mm { get; init; }
    public required uint GuestPageStart { get; init; }
    public int ExecRefCount { get; set; }
    public int WriteRefCount { get; set; }
}

internal sealed class SmallPageSet
{
    private HashSet<uint>? _overflow;
    private int _count;
    private uint _item0;
    private uint _item1;
    private uint _item2;
    private uint _item3;

    public int Count => _overflow?.Count ?? _count;

    public bool Add(uint value)
    {
        if (_overflow != null)
            return _overflow.Add(value);

        if (ContainsInline(value))
            return false;

        switch (_count)
        {
            case 0:
                _item0 = value;
                _count = 1;
                return true;
            case 1:
                _item1 = value;
                _count = 2;
                return true;
            case 2:
                _item2 = value;
                _count = 3;
                return true;
            case 3:
                _item3 = value;
                _count = 4;
                return true;
            default:
                _overflow = new HashSet<uint>(5) { _item0, _item1, _item2, _item3, value };
                _count = 0;
                _item0 = 0;
                _item1 = 0;
                _item2 = 0;
                _item3 = 0;
                return true;
        }
    }

    public bool Remove(uint value)
    {
        if (_overflow != null)
            return _overflow.Remove(value);

        var index = IndexOfInline(value);
        if (index < 0)
            return false;

        var lastIndex = _count - 1;
        if (index != lastIndex)
            SetInline(index, GetInline(lastIndex));
        SetInline(lastIndex, 0);
        _count--;
        return true;
    }

    public void Visit<TState>(ref TState state, SmallPageSetVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (_overflow != null)
        {
            foreach (var value in _overflow)
                visitor(value, ref state);
            return;
        }

        for (var i = 0; i < _count; i++)
            visitor(GetInline(i), ref state);
    }

    private bool ContainsInline(uint value)
    {
        return IndexOfInline(value) >= 0;
    }

    private int IndexOfInline(uint value)
    {
        for (var i = 0; i < _count; i++)
            if (GetInline(i) == value)
                return i;
        return -1;
    }

    private uint GetInline(int index)
    {
        return index switch
        {
            0 => _item0,
            1 => _item1,
            2 => _item2,
            3 => _item3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    private void SetInline(int index, uint value)
    {
        switch (index)
        {
            case 0:
                _item0 = value;
                break;
            case 1:
                _item1 = value;
                break;
            case 2:
                _item2 = value;
                break;
            case 3:
                _item3 = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

internal sealed class TbCohMmBucket
{
    public required VMAManager Mm { get; init; }
    public SmallPageSet ExecPages { get; } = new();
    public SmallPageSet WriterPages { get; } = new();

    public bool IsEmpty => ExecPages.Count == 0 && WriterPages.Count == 0;
}

internal sealed class HostPageTbCohIndex
{
    private readonly Dictionary<TbCohMmPageKey, TbCohMmPageEntry> _byMmPage = [];
    private readonly Dictionary<VMAManager, TbCohMmBucket> _byMm = [];
    private readonly Dictionary<nuint, int> _execIdentityCounts = [];
    private int _execIdentityCount;
    private nuint _singleExecIdentity;

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

    private TbCohMmBucket GetOrAddBucket(VMAManager mm)
    {
        if (_byMm.TryGetValue(mm, out var existing))
            return existing;

        var created = new TbCohMmBucket { Mm = mm };
        _byMm[mm] = created;
        return created;
    }

    private void AddExecIdentity(nuint identity)
    {
        if (_execIdentityCounts.TryGetValue(identity, out var existing))
        {
            _execIdentityCounts[identity] = existing + 1;
            return;
        }

        _execIdentityCounts[identity] = 1;
        _execIdentityCount++;
        _singleExecIdentity = _execIdentityCount == 1 ? identity : 0;
    }

    private void RemoveExecIdentity(nuint identity)
    {
        if (!_execIdentityCounts.TryGetValue(identity, out var existing))
            return;

        if (existing > 1)
        {
            _execIdentityCounts[identity] = existing - 1;
            return;
        }

        _execIdentityCounts.Remove(identity);
        _execIdentityCount--;
        if (_execIdentityCount == 1)
        {
            foreach (var remainingIdentity in _execIdentityCounts.Keys)
            {
                _singleExecIdentity = remainingIdentity;
                return;
            }
        }

        _singleExecIdentity = 0;
    }

    public void AddRoles(VMAManager mm, uint guestPageStart, Protection perms)
    {
        var hasExec = HasExecRole(perms);
        var hasWrite = HasWriteRole(perms);
        if (!hasExec && !hasWrite)
            return;

        var key = new TbCohMmPageKey(mm, guestPageStart);
        if (!_byMmPage.TryGetValue(key, out var entry))
        {
            entry = new TbCohMmPageEntry
            {
                Mm = mm,
                GuestPageStart = guestPageStart
            };
            _byMmPage[key] = entry;
        }

        TbCohMmBucket? bucket = null;
        if (hasExec)
        {
            if (entry.ExecRefCount == 0)
            {
                bucket = GetOrAddBucket(mm);
                bucket.ExecPages.Add(guestPageStart);
                AddExecIdentity(GetCoherenceIdentity(mm));
            }

            entry.ExecRefCount++;
        }

        if (hasWrite)
        {
            if (entry.WriteRefCount == 0)
            {
                bucket ??= GetOrAddBucket(mm);
                bucket.WriterPages.Add(guestPageStart);
            }

            entry.WriteRefCount++;
        }
    }

    public void RemoveRoles(VMAManager mm, uint guestPageStart, Protection perms)
    {
        var hasExec = HasExecRole(perms);
        var hasWrite = HasWriteRole(perms);
        if (!hasExec && !hasWrite)
            return;

        var key = new TbCohMmPageKey(mm, guestPageStart);
        if (!_byMmPage.TryGetValue(key, out var entry))
            return;

        _byMm.TryGetValue(mm, out var bucket);
        if (hasExec && entry.ExecRefCount > 0)
        {
            entry.ExecRefCount--;
            if (entry.ExecRefCount == 0)
            {
                bucket?.ExecPages.Remove(guestPageStart);
                RemoveExecIdentity(GetCoherenceIdentity(mm));
            }
        }

        if (hasWrite && entry.WriteRefCount > 0)
        {
            entry.WriteRefCount--;
            if (entry.WriteRefCount == 0)
                bucket?.WriterPages.Remove(guestPageStart);
        }

        if (entry.ExecRefCount == 0 && entry.WriteRefCount == 0)
            _byMmPage.Remove(key);

        if (bucket is { IsEmpty: true })
            _byMm.Remove(mm);
    }

    public void UpdateRoles(VMAManager mm, uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        if (HasExecRole(oldPerms) == HasExecRole(newPerms) && HasWriteRole(oldPerms) == HasWriteRole(newPerms))
            return;

        RemoveRoles(mm, guestPageStart, oldPerms);
        AddRoles(mm, guestPageStart, newPerms);
    }

    public TbCohExecSummary GetExecSummary()
    {
        return new TbCohExecSummary(_execIdentityCount != 0, _execIdentityCount > 1, _singleExecIdentity);
    }

    public void VisitWriterPages<TState>(ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var bucket in _byMm.Values)
        {
            var mm = bucket.Mm;
            bucket.WriterPages.Visit(ref state, (uint guestPageStart, ref TState innerState) =>
            {
                visitor(mm, guestPageStart, ref innerState);
            });
        }
    }

    public void VisitExecPages<TState>(ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var bucket in _byMm.Values)
        {
            var mm = bucket.Mm;
            bucket.ExecPages.Visit(ref state, (uint guestPageStart, ref TState innerState) =>
            {
                visitor(mm, guestPageStart, ref innerState);
            });
        }
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

internal readonly record struct RmapHit(
    HostPage HostPage,
    VMAManager Mm,
    VmArea Vma,
    HostPageOwnerKind OwnerKind,
    uint PageIndex,
    uint GuestPageStart);

internal delegate void HostPageRmapVisitor<TState>(HostPage hostPage, in HostPageRmapRef rmapRef, ref TState state);
internal delegate void TbCohMmPageVisitor<TState>(VMAManager mm, uint guestPageStart, ref TState state);
internal delegate void SmallPageSetVisitor<TState>(uint pageStart, ref TState state);

internal readonly record struct TbCohExecSummary(bool HasExecPeer, bool HasMultipleExecIdentities, nuint ExecIdentity);

internal sealed class HostPage
{
    private readonly Lock _gate = new();
    private readonly List<HostPageOwnerRef> _ownerRefs = [];
    private readonly List<HostPageRmapRef> _rmapRefs = [];
    private readonly Dictionary<HostPageRmapKey, int> _rmapRefIndices = [];
    private readonly HostPageTbCohIndex _tbCohIndex = new();

    public HostPage(IntPtr ptr, HostPageKind kind)
    {
        Ptr = ptr;
        Kind = kind;
    }

    public IntPtr Ptr { get; }
    public HostPageKind Kind { get; private set; }
    public bool Dirty { get; set; }
    public bool Uptodate { get; set; } = true;
    public bool Writeback { get; set; }
    public int MapCount { get; set; }
    public int PinCount { get; set; }
    public int RefCount { get; set; }
    // Monotonic timestamp used for recency comparisons in reclaim and stats paths.
    public long LastAccessTimestamp { get; set; } = MonotonicTime.GetTimestamp();

    public void UpgradeKind(HostPageKind preferredKind)
    {
        if (Kind == preferredKind)
            return;

        if (Kind == HostPageKind.Zero || preferredKind == HostPageKind.Zero)
            throw new InvalidOperationException(
                $"Host page kind mismatch for {Ptr}: existing={Kind}, requested={preferredKind}.");

        // The owner graph is authoritative. A live page may be reachable from both
        // page-cache and anon owners during COW / truncate lifecycle transitions.
        // Zero pages remain exclusive and are guarded above.
    }

    public void AddOwnerRef(HostPageOwnerRef ownerRef)
    {
        lock (_gate)
        {
            foreach (var existing in _ownerRefs)
                if (existing.Matches(ownerRef))
                    return;

            _ownerRefs.Add(ownerRef);
        }
    }

    public void RemoveOwnerRef(HostPageOwnerRef ownerRef)
    {
        lock (_gate)
        {
            for (var i = 0; i < _ownerRefs.Count; i++)
                if (_ownerRefs[i].Matches(ownerRef))
                {
                    _ownerRefs.RemoveAt(i);
                    break;
                }
        }

        HostPageManager.TryRemoveIfUnused(this);
    }

    public bool HasOwnerRefs
    {
        get
        {
            lock (_gate)
            {
                return _ownerRefs.Count != 0;
            }
        }
    }

    public void AddOrUpdateRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex,
        uint guestPageStart)
    {
        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        lock (_gate)
        {
            if (_rmapRefIndices.TryGetValue(key, out var existingIndex))
            {
                var existing = _rmapRefs[existingIndex];
                if (existing.GuestPageStart == guestPageStart)
                    return;

                _tbCohIndex.RemoveRoles(mm, existing.GuestPageStart, existing.Vma.Perms);
                existing.GuestPageStart = guestPageStart;
                _rmapRefs[existingIndex] = existing;
                _tbCohIndex.AddRoles(mm, guestPageStart, vma.Perms);
                return;
            }

            _rmapRefIndices.Add(key, _rmapRefs.Count);
            _rmapRefs.Add(new HostPageRmapRef
            {
                Mm = mm,
                Vma = vma,
                OwnerKind = ownerKind,
                PageIndex = pageIndex,
                GuestPageStart = guestPageStart
            });
            _tbCohIndex.AddRoles(mm, guestPageStart, vma.Perms);
        }
    }

    public void RemoveRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex)
    {
        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        lock (_gate)
        {
            if (!_rmapRefIndices.Remove(key, out var index))
                return;

            var removed = _rmapRefs[index];
            _tbCohIndex.RemoveRoles(mm, removed.GuestPageStart, removed.Vma.Perms);
            var lastIndex = _rmapRefs.Count - 1;
            if (index != lastIndex)
            {
                var swapped = _rmapRefs[lastIndex];
                _rmapRefs[index] = swapped;
                _rmapRefIndices[swapped.GetKey()] = index;
            }

            _rmapRefs.RemoveAt(lastIndex);
        }
    }

    public void UpdateTbCohRolesForRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex,
        uint guestPageStart, Protection oldPerms, Protection newPerms)
    {
        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        lock (_gate)
        {
            if (!_rmapRefIndices.ContainsKey(key))
                return;

            _tbCohIndex.UpdateRoles(mm, guestPageStart, oldPerms, newPerms);
        }
    }

    public TbCohExecSummary GetTbCohExecSummary()
    {
        lock (_gate)
        {
            return _tbCohIndex.GetExecSummary();
        }
    }

    public void VisitTbCohWriterPages<TState>(ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_gate)
            _tbCohIndex.VisitWriterPages(ref state, visitor);
    }

    public void VisitTbCohExecPages<TState>(ref TState state, TbCohMmPageVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_gate)
            _tbCohIndex.VisitExecPages(ref state, visitor);
    }

    public void CollectRmapHits(List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
            foreach (var rmapRef in _rmapRefs)
                output.Add(new RmapHit(this, rmapRef.Mm, rmapRef.Vma, rmapRef.OwnerKind, rmapRef.PageIndex,
                    rmapRef.GuestPageStart));
    }

    public void VisitRmapRefs<TState>(ref TState state, HostPageRmapVisitor<TState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_gate)
            foreach (var rmapRef in _rmapRefs)
                visitor(this, rmapRef, ref state);
    }
}

internal static class HostPageManager
{
    private sealed class State
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<IntPtr, HostPage> Pages = [];
    }

    private sealed class ScopeRestore : IDisposable
    {
        private readonly State? _previous;

        public ScopeRestore(State? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            ScopedState.Value = _previous;
        }
    }

    private static readonly AsyncLocal<State?> ScopedState = new();
    private static readonly State DefaultState = new();

    private static State CurrentState => ScopedState.Value ?? DefaultState;

    internal static IDisposable BeginIsolatedScope()
    {
        var previous = ScopedState.Value;
        ScopedState.Value = new State();
        return new ScopeRestore(previous);
    }

    internal static HostPage GetOrCreate(IntPtr ptr, HostPageKind preferredKind)
    {
        if (ptr == IntPtr.Zero)
            throw new ArgumentException("Host page pointer must be non-zero.", nameof(ptr));

        var state = CurrentState;
        lock (state.Gate)
        {
            if (state.Pages.TryGetValue(ptr, out var existing))
            {
                existing.UpgradeKind(preferredKind);
                return existing;
            }

            var hostPage = new HostPage(ptr, preferredKind);
            state.Pages[ptr] = hostPage;
            return hostPage;
        }
    }

    internal static bool TryLookup(IntPtr ptr, out HostPage page)
    {
        var state = CurrentState;
        lock (state.Gate)
        {
            return state.Pages.TryGetValue(ptr, out page!);
        }
    }

    internal static HostPage Retain(IntPtr ptr, HostPageKind preferredKind)
    {
        var page = GetOrCreate(ptr, preferredKind);
        page.RefCount++;
        return page;
    }

    internal static bool TryRetainExisting(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return false;

        page.RefCount++;
        return true;
    }

    internal static void Release(IntPtr ptr)
    {
        if (!TryLookup(ptr, out var page))
            return;

        if (page.RefCount > 0)
            page.RefCount--;
        TryRemoveIfUnused(page);
    }

    internal static void TryRemoveIfUnused(HostPage page)
    {
        if (page.Kind == HostPageKind.Zero)
            return;
        if (page.RefCount > 0 || page.MapCount > 0 || page.PinCount > 0 || page.HasOwnerRefs)
            return;

        var state = CurrentState;
        lock (state.Gate)
        {
            if (!state.Pages.TryGetValue(page.Ptr, out var existing) || !ReferenceEquals(existing, page))
                return;
            if (page.RefCount > 0 || page.MapCount > 0 || page.PinCount > 0 || page.HasOwnerRefs)
                return;

            state.Pages.Remove(page.Ptr);
        }
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
