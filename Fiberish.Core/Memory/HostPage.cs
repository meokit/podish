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

internal sealed class HostPage
{
    private readonly Lock _gate = new();
    private readonly List<HostPageOwnerRef> _ownerRefs = [];
    private readonly List<HostPageRmapRef> _rmapRefs = [];
    private readonly Dictionary<HostPageRmapKey, int> _rmapRefIndices = [];

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
    public long LastAccessTicks { get; set; } = DateTime.UtcNow.Ticks;

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

                existing.GuestPageStart = guestPageStart;
                _rmapRefs[existingIndex] = existing;
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
        }
    }

    public void RemoveRmapRef(VMAManager mm, VmArea vma, HostPageOwnerKind ownerKind, uint pageIndex)
    {
        var key = new HostPageRmapKey(mm, vma, ownerKind, pageIndex);
        lock (_gate)
        {
            if (!_rmapRefIndices.Remove(key, out var index))
                return;

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

    public void CollectRmapHits(List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
            foreach (var rmapRef in _rmapRefs)
                output.Add(new RmapHit(this, rmapRef.Mm, rmapRef.Vma, rmapRef.OwnerKind, rmapRef.PageIndex,
                    rmapRef.GuestPageStart));
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
}
