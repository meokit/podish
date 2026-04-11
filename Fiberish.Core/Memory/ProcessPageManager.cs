using Fiberish.Native;

namespace Fiberish.Memory;

public enum MappedPageOwnerKind
{
    AddressSpace,
    AnonVma
}

internal sealed class MappedPageBinding
{
    public required IntPtr Ptr { get; init; }
    public required MappedPageOwnerKind OwnerKind { get; init; }
    public AddressSpace? Mapping { get; init; }
    public AnonVma? AnonVma { get; init; }
    public ResidentPageRecord? Page { get; init; }
    public uint PageIndex { get; init; }

    internal static MappedPageBinding FromAddressSpacePage(AddressSpace mapping, uint pageIndex, ResidentPageRecord pageRecord)
    {
        return new MappedPageBinding
        {
            Ptr = pageRecord.Ptr,
            OwnerKind = MappedPageOwnerKind.AddressSpace,
            Mapping = mapping,
            Page = pageRecord,
            PageIndex = pageIndex
        };
    }

    internal static MappedPageBinding FromAnonVmaPage(AnonVma anonVma, uint pageIndex, ResidentPageRecord pageRecord)
    {
        return new MappedPageBinding
        {
            Ptr = pageRecord.Ptr,
            OwnerKind = MappedPageOwnerKind.AnonVma,
            AnonVma = anonVma,
            Page = pageRecord,
            PageIndex = pageIndex
        };
    }
}

public sealed class ProcessPageManager
{
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly Dictionary<uint, MappedPageBinding> _pages = [];

    public ProcessPageManager(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        _memoryContext = memoryContext;
    }

    public bool TryGet(uint pageAddr, out IntPtr ptr)
    {
        if (_pages.TryGetValue(pageAddr, out var page))
        {
            ptr = page.Ptr;
            return true;
        }

        ptr = IntPtr.Zero;
        return false;
    }

    internal bool TryGetBinding(uint pageAddr, out MappedPageBinding? binding)
    {
        if (_pages.TryGetValue(pageAddr, out var existing))
        {
            binding = existing;
            return true;
        }

        binding = null;
        return false;
    }

    internal bool AddBinding(uint pageAddr, MappedPageBinding binding, out bool addedRef)
    {
        addedRef = false;
        if (binding.Ptr == IntPtr.Zero) return false;
        if (_pages.TryGetValue(pageAddr, out var existing)) return existing.Ptr == binding.Ptr;

        var boundHostPage = binding.Page?.HostPage ?? _memoryContext.HostPages.GetRequired(binding.Ptr);
        boundHostPage.MapCount++;
        _pages[pageAddr] = binding;
        addedRef = true;
        return true;
    }

    public void Release(uint pageAddr, bool preserveOwnerBinding = false)
    {
        if (!_pages.Remove(pageAddr, out var binding)) return;
        var hostPage = binding.Page?.HostPage ?? _memoryContext.HostPages.GetRequired(binding.Ptr);

        if (hostPage.MapCount > 0)
            hostPage.MapCount--;

        if (!preserveOwnerBinding &&
            binding.OwnerKind == MappedPageOwnerKind.AnonVma &&
            binding.AnonVma is { } anonVma &&
            binding.Page is { } anonPage &&
            anonPage.HostPage is { MapCount: <= 0, PinCount: <= 0, OwnerResidentCount: <= 1 })
            anonVma.RemovePageIfMatches(binding.PageIndex, anonPage);

        _memoryContext.HostPages.TryRemoveIfUnused(hostPage);
    }

    public void ReleaseRange(uint addr, uint length, bool preserveOwnerBinding = false)
    {
        if (length == 0) return;
        var start = addr & LinuxConstants.PageMask;
        var end = (addr + length + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var p = start; p < end; p += LinuxConstants.PageSize) Release(p, preserveOwnerBinding);
    }

    public IReadOnlyList<uint> SnapshotMappedPages()
    {
        if (_pages.Count == 0) return Array.Empty<uint>();
        return _pages.Keys.ToArray();
    }
}
