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

internal readonly record struct RmapHit(
    HostPage HostPage,
    VMAManager Mm,
    VmArea Vma,
    HostPageOwnerKind OwnerKind,
    uint PageIndex);

internal sealed class HostPage
{
    private readonly Lock _gate = new();
    private readonly List<HostPageOwnerRef> _ownerRefs = [];

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

    public void CollectRmapHits(List<RmapHit> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        List<HostPageOwnerRef> ownerRefs;
        lock (_gate)
        {
            ownerRefs = _ownerRefs.ToList();
        }

        var seen = new HashSet<(VMAManager Mm, VmArea Vma, HostPageOwnerKind OwnerKind, uint PageIndex)>();
        foreach (var ownerRef in ownerRefs)
        {
            switch (ownerRef.OwnerKind)
            {
                case HostPageOwnerKind.AddressSpace:
                    ownerRef.Mapping?.CollectRmapHits(this, ownerRef.PageIndex, output, seen);
                    break;
                case HostPageOwnerKind.AnonVma:
                    ownerRef.AnonVma?.CollectRmapHits(this, ownerRef.PageIndex, output, seen);
                    break;
            }
        }
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
