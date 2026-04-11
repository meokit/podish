using System.Diagnostics;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Memory;

public enum FaultResult
{
    Handled = 0,
    Segv = 1,
    BusError = 2,
    Oom = 3
}

public class VMAManager
{
    private const int MaxInvalidationEntries = 4096;
    private static readonly ILogger Logger = Logging.CreateLogger<VMAManager>();
    private static long _cowAllocFirstCount;
    private static long _cowAllocReplaceCount;
    private readonly Dictionary<Inode, int> _mappedInodeRefCounts = [];

    private readonly List<CodeCacheResetEntry> _pendingCodeCacheResets = [];
    private readonly HashSet<uint> _tbWpPages = [];
    private readonly List<VmArea> _vmas = [];
    private int _futexKeyRefCount = 1;
    private VmArea? _lastFaultVma;
    private long _mapSequence;
    private int _sharedRefCount = 1;

    public VMAManager(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        MemoryContext = memoryContext;
        PageMapping = new ProcessPageManager(memoryContext);
    }

    public ProcessPageManager PageMapping { get; }
    internal ProcessAddressSpaceHandle? AddressSpaceHandle { get; private set; }
    internal MemoryRuntimeContext MemoryContext { get; }

    internal nuint AddressSpaceIdentity => AddressSpaceHandle?.Identity ?? 0;

    public long CurrentMapSequence => Interlocked.Read(ref _mapSequence);

    public IReadOnlyList<VmArea> VMAs => _vmas;

    public static (long First, long Replace) GetCowAllocationCounters()
    {
        return (Interlocked.Read(ref _cowAllocFirstCount), Interlocked.Read(ref _cowAllocReplaceCount));
    }

    public int GetSharedRefCount()
    {
        return Volatile.Read(ref _sharedRefCount);
    }

    internal void AcquireFutexKeyRef()
    {
        Interlocked.Increment(ref _futexKeyRefCount);
    }

    internal void ReleaseFutexKeyRef()
    {
        var remaining = Interlocked.Decrement(ref _futexKeyRefCount);
        if (remaining >= 0) return;
        Interlocked.Exchange(ref _futexKeyRefCount, 0);
    }

    internal long BumpMapSequence()
    {
        return Interlocked.Increment(ref _mapSequence);
    }

    internal void RecordCodeCacheResetRange(long sequence, uint addr, uint len)
    {
        if (len == 0) return;
        var (start, endExclusive) = ComputePageAlignedRange(addr, len);
        if (endExclusive <= start) return;
        var range = new NativeRange((uint)start, (uint)(endExclusive - start));
        _pendingCodeCacheResets.Add(new CodeCacheResetEntry(sequence, range));
        if (_pendingCodeCacheResets.Count > MaxInvalidationEntries)
            CompactCodeCacheResetRanges(sequence);
    }

    internal long CollectCodeCacheResetRangesSince(long seenSequence, List<NativeRange> output)
    {
        output.Clear();
        var current = CurrentMapSequence;
        if (seenSequence >= current) return current;

        foreach (var entry in _pendingCodeCacheResets)
        {
            if (entry.Sequence <= seenSequence) continue;
            output.Add(entry.Range);
        }

        if (output.Count > 1)
            MergeRangesInPlace(output);

        return current;
    }

    internal void PruneCodeCacheResetRanges(long minSeenSequence)
    {
        if (_pendingCodeCacheResets.Count == 0) return;
        var removeCount = 0;
        while (removeCount < _pendingCodeCacheResets.Count &&
               _pendingCodeCacheResets[removeCount].Sequence <= minSeenSequence)
            removeCount++;
        if (removeCount > 0)
            _pendingCodeCacheResets.RemoveRange(0, removeCount);
    }

    internal void MarkTbWp(uint pageStart)
    {
        _tbWpPages.Add(pageStart & LinuxConstants.PageMask);
    }

    internal void UnmarkTbWp(uint pageStart)
    {
        _tbWpPages.Remove(pageStart & LinuxConstants.PageMask);
    }

    internal IReadOnlyList<uint> SnapshotTbWpPages()
    {
        if (_tbWpPages.Count == 0) return Array.Empty<uint>();
        return _tbWpPages.ToArray();
    }

    internal void ClearTbWpRange(uint addr, uint len)
    {
        if (len == 0 || _tbWpPages.Count == 0) return;
        var (start, endExclusive) = ComputePageAlignedRange(addr, len);
        if (endExclusive <= start) return;

        _tbWpPages.RemoveWhere(page => page >= start && page < endExclusive);
    }

    private void CompactCodeCacheResetRanges(long sequence)
    {
        if (_pendingCodeCacheResets.Count == 0) return;
        var ranges = new List<NativeRange>(_pendingCodeCacheResets.Count);
        foreach (var entry in _pendingCodeCacheResets)
            ranges.Add(entry.Range);
        MergeRangesInPlace(ranges);
        _pendingCodeCacheResets.Clear();
        foreach (var range in ranges)
            _pendingCodeCacheResets.Add(new CodeCacheResetEntry(sequence, range));
    }

    public int AddSharedRef()
    {
        return Interlocked.Increment(ref _sharedRefCount);
    }

    public int ReleaseSharedRef(Engine engine)
    {
        var remaining = Interlocked.Decrement(ref _sharedRefCount);
        if (remaining > 0) return remaining;
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _sharedRefCount, 0);
            return 0;
        }

        Clear(engine);
        AddressSpaceHandle?.Dispose();
        AddressSpaceHandle = null;
        return 0;
    }

    internal void BindAddressSpaceHandle(ProcessAddressSpaceHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (AddressSpaceHandle == null)
        {
            AddressSpaceHandle = handle;
            return;
        }

        if (AddressSpaceHandle.Identity == handle.Identity)
        {
            handle.Dispose();
            return;
        }

        AddressSpaceHandle.Dispose();
        AddressSpaceHandle = handle;
    }

    internal void BindOrAssertAddressSpaceHandle(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (AddressSpaceHandle == null)
        {
            AddressSpaceHandle = ProcessAddressSpaceHandle.CaptureAttachedEngine(engine);
            return;
        }

        if (!AddressSpaceHandle.IsAttachedTo(engine))
            throw new InvalidOperationException(
                $"Engine MMU identity {engine.CurrentMmuIdentityInternal} does not match address-space MMU identity {AddressSpaceHandle.Identity}.");
    }

    internal bool TryAttachEngineToBoundAddressSpace(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (AddressSpaceHandle == null)
            return false;

        if (AddressSpaceHandle.IsAttachedTo(engine))
            return true;

        AddressSpaceHandle.AttachEngine(engine);
        return true;
    }

    private static Inode? ResolveMappedInode(VmArea vma)
    {
        return vma.File?.OpenedInode;
    }

    private static Inode? ResolveCurrentFileBacking(LinuxFile? file)
    {
        if (file?.OpenedInode is OverlayInode overlayInode)
            return overlayInode.ResolveMmapSource(file) ?? file.OpenedInode;

        return file?.OpenedInode;
    }

    private static MappingBackedInode? ResolveCurrentFileMappingBacking(LinuxFile? file)
    {
        return ResolveCurrentFileBacking(file) as MappingBackedInode;
    }

    private bool SyncFileBackedVmaMapping(VmArea vma)
    {
        var backingInode = ResolveCurrentFileMappingBacking(vma.File);
        if (backingInode == null)
            return false;

        var resolvedMapping = backingInode.AcquireMappingRef();
        if (ReferenceEquals(vma.VmMapping, resolvedMapping))
        {
            resolvedMapping.Release();
            return false;
        }

        vma.VmMapping?.RemoveRmapAttachments(vma);
        vma.VmMapping?.Release();
        vma.VmMapping = resolvedMapping;
        RegisterVmAreaMappingAttachment(vma);
        return true;
    }

    private static AnonVma? ShareVmAnonVmaForSplit(VmArea vma)
    {
        var privateObject = vma.VmAnonVma;
        privateObject?.AddRef();
        return privateObject;
    }

    private static (uint StartPageIndex, uint EndPageIndexExclusive) GetObjectPageRange(VmArea vma, uint guestStart,
        uint guestEndExclusive)
    {
        var startPage = guestStart & LinuxConstants.PageMask;
        var endPage = (guestEndExclusive + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        var startPageIndex = vma.GetPageIndex(startPage);
        var endPageIndexExclusive = startPageIndex + (endPage - startPage) / LinuxConstants.PageSize;
        return (startPageIndex, endPageIndexExclusive);
    }

    private static (uint StartPageIndex, uint EndPageIndexExclusive) GetVmAreaPageRange(VmArea vma)
    {
        if (vma.Length == 0)
            return (0, 0);

        var startPageIndex = vma.GetPageIndex(vma.Start);
        return (startPageIndex, startPageIndex + vma.Length / LinuxConstants.PageSize);
    }

    private void RegisterVmAreaMappingAttachment(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        if (vma.VmMapping == null || vma.Length == 0)
            return;

        var (startPageIndex, endPageIndexExclusive) = GetVmAreaPageRange(vma);
        vma.VmMapping.AddRmapAttachment(this, vma, startPageIndex, endPageIndexExclusive, tbCohWorkSet);
    }

    private void RegisterVmAreaAnonAttachment(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        if (vma.VmAnonVma == null || vma.Length == 0)
            return;

        var (startPageIndex, endPageIndexExclusive) = GetVmAreaPageRange(vma);
        vma.VmAnonVma.AddRmapAttachment(this, vma, startPageIndex, endPageIndexExclusive, tbCohWorkSet);
    }

    private void RegisterVmAreaAttachments(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        RegisterVmAreaMappingAttachment(vma, tbCohWorkSet);
        RegisterVmAreaAnonAttachment(vma, tbCohWorkSet);
    }

    private static void UnregisterVmAreaAttachments(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        vma.VmMapping?.RemoveRmapAttachments(vma, tbCohWorkSet);
        vma.VmAnonVma?.RemoveRmapAttachments(vma, tbCohWorkSet);
    }

    private static void QueueUnmappedObjectPages(VmArea vma, uint guestStart, uint guestEndExclusive,
        List<(AnonVma AnonVma, uint StartPageIndex, uint EndPageIndexExclusive)> pendingObjectPageReleases)
    {
        ArgumentNullException.ThrowIfNull(pendingObjectPageReleases);
        if (guestStart >= guestEndExclusive || vma.VmAnonVma == null)
            return;

        var (startPageIndex, endPageIndexExclusive) = GetObjectPageRange(vma, guestStart, guestEndExclusive);
        if (startPageIndex >= endPageIndexExclusive)
            return;

        vma.VmAnonVma.AddRef();
        pendingObjectPageReleases.Add((vma.VmAnonVma, startPageIndex, endPageIndexExclusive));
    }

    private static void ReleasePendingObjectPages(
        List<(AnonVma AnonVma, uint StartPageIndex, uint EndPageIndexExclusive)>? pendingObjectPageReleases,
        bool releasePages)
    {
        if (pendingObjectPageReleases == null)
            return;

        foreach (var (anonVma, startPageIndex, endPageIndexExclusive) in pendingObjectPageReleases)
            try
            {
                if (releasePages)
                    anonVma.RemovePagesInRange(startPageIndex, endPageIndexExclusive);
            }
            finally
            {
                anonVma.Release();
            }
    }

    private static uint GetVmaPageIndex(VmArea vma, uint guestPageStart)
    {
        return vma.GetPageIndex(guestPageStart);
    }

    internal void CollectManagedHostPagesInRange(uint addr, uint len, HashSet<IntPtr> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (len == 0) return;

        var endExclusive = ComputeRangeEnd(addr, len);
        foreach (var vma in FindVmAreasInRange(addr, endExclusive))
        {
            var overlapStart = Math.Max(vma.Start, addr);
            var overlapEnd = Math.Min(vma.End, endExclusive);
            CollectHostPagesForVmaRange(vma, overlapStart, overlapEnd, output);
        }
    }

    private static void CollectHostPagesForVmaRange(VmArea vma, uint guestStart, uint guestEndExclusive,
        HashSet<IntPtr> output)
    {
        ArgumentNullException.ThrowIfNull(vma);
        ArgumentNullException.ThrowIfNull(output);
        if (guestStart >= guestEndExclusive) return;

        var startPage = guestStart & LinuxConstants.PageMask;
        var endPageExclusive = (guestEndExclusive + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var page = startPage; page < endPageExclusive; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.GetPageIndex(page);
            var hostPagePtr = vma.VmAnonVma?.PeekPage(pageIndex) ?? vma.VmMapping?.PeekPage(pageIndex) ?? IntPtr.Zero;
            if (hostPagePtr != IntPtr.Zero)
                output.Add(hostPagePtr);
        }
    }

    private void UpdateTbCohRolesForVmaRange(VmArea vma, uint guestStart, uint guestEndExclusive, Protection oldPerms,
        Protection newPerms, TbCohWorkSet? tbCohWorkSet = null)
    {
        if (guestStart >= guestEndExclusive)
            return;
        if (((oldPerms ^ newPerms) & (Protection.Exec | Protection.Write)) == 0)
            return;

        var startPage = guestStart & LinuxConstants.PageMask;
        var endPageExclusive = (guestEndExclusive + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var page = startPage; page < endPageExclusive; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.GetPageIndex(page);
            IntPtr hostPagePtr;
            HostPageOwnerKind ownerKind;
            if ((hostPagePtr = vma.VmAnonVma?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            {
                ownerKind = HostPageOwnerKind.AnonVma;
            }
            else if ((hostPagePtr = vma.VmMapping?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            {
                ownerKind = HostPageOwnerKind.AddressSpace;
            }
            else
            {
                continue;
            }

            var changed = MemoryContext.HostPages.UpdateTbCohRolesForRmapRef(hostPagePtr, this, vma, ownerKind, pageIndex,
                page, oldPerms, newPerms);
            tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
        }
    }

    private void RebindRmapRefsForVmaRange(VmArea sourceVma, VmArea targetVma, uint guestStart, uint guestEndExclusive,
        Protection oldPerms, Protection newPerms, TbCohWorkSet? tbCohWorkSet = null)
    {
        if (guestStart >= guestEndExclusive)
            return;

        var startPage = guestStart & LinuxConstants.PageMask;
        var endPageExclusive = (guestEndExclusive + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var page = startPage; page < endPageExclusive; page += LinuxConstants.PageSize)
        {
            var pageIndex = sourceVma.GetPageIndex(page);
            IntPtr hostPagePtr;
            HostPageOwnerKind ownerKind;
            if ((hostPagePtr = sourceVma.VmAnonVma?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            {
                ownerKind = HostPageOwnerKind.AnonVma;
            }
            else if ((hostPagePtr = sourceVma.VmMapping?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            {
                ownerKind = HostPageOwnerKind.AddressSpace;
            }
            else
            {
                continue;
            }

            var changed = MemoryContext.HostPages.RebindRmapRef(hostPagePtr, this, sourceVma, targetVma, ownerKind,
                pageIndex, page, oldPerms, newPerms);
            tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
        }
    }

    private void ResetVmAreaAttachmentsForSplit(VmArea retainedVma, VmArea? extraVma0 = null, VmArea? extraVma1 = null)
    {
        retainedVma.VmMapping?.ResetRmapAttachmentsForSplit(this, retainedVma, extraVma0, extraVma1);
        retainedVma.VmAnonVma?.ResetRmapAttachmentsForSplit(this, retainedVma, extraVma0, extraVma1);
    }

    private MappedPageBinding CreateResolvedPageBinding(VmArea vma, uint pageIndex, IntPtr pagePtr)
    {
        if (vma.VmAnonVma?.PeekVmPage(pageIndex) is { } privatePage && privatePage.Ptr == pagePtr)
            return MappedPageBinding.FromAnonVmaPage(vma.VmAnonVma, pageIndex, privatePage);

        if (vma.VmMapping?.PeekVmPage(pageIndex) is { } sharedPage && sharedPage.Ptr == pagePtr)
            return MappedPageBinding.FromAddressSpacePage(vma.VmMapping, pageIndex, sharedPage);

        throw new InvalidOperationException(
            $"Failed to resolve managed page binding for VMA '{vma.Name}' pageIndex={pageIndex} ptr={pagePtr}.");
    }

    private void TrackMappedInodeOnVmaAdded(VmArea vma)
    {
        var inode = ResolveMappedInode(vma);
        if (inode == null) return;
        if (_mappedInodeRefCounts.TryGetValue(inode, out var existing))
        {
            _mappedInodeRefCounts[inode] = existing + 1;
            return;
        }

        _mappedInodeRefCounts[inode] = 1;
        inode.RegisterMappedAddressSpace(this);
    }

    private void TrackMappedInodeOnVmaRemoved(VmArea vma)
    {
        var inode = ResolveMappedInode(vma);
        if (inode == null) return;
        if (!_mappedInodeRefCounts.TryGetValue(inode, out var existing))
            return;

        if (existing <= 1)
        {
            _mappedInodeRefCounts.Remove(inode);
            inode.UnregisterMappedAddressSpace(this);
            return;
        }

        _mappedInodeRefCounts[inode] = existing - 1;
    }

    private void InsertVmaSorted(VmArea vma)
    {
        ValidateVmaBindings(vma);

        var left = 0;
        var right = _vmas.Count - 1;
        var insertIndex = _vmas.Count;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            if (_vmas[mid].Start >= vma.End)
            {
                insertIndex = mid;
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        _vmas.Insert(insertIndex, vma);
        TrackMappedInodeOnVmaAdded(vma);
    }

    private static void ValidateVmaBindings(VmArea vma)
    {
        if ((vma.Flags & MapFlags.Shared) == 0)
            return;

        if (vma.File?.OpenedInode != null)
            return;

        VfsDebugTrace.FailInvariant(
            $"Shared VMA missing file backing start=0x{vma.Start:X8} end=0x{vma.End:X8} flags={vma.Flags} perms={vma.Perms}");
    }

    public VmArea? FindVmArea(uint addr)
    {
        var cached = _lastFaultVma;
        if (cached != null && addr >= cached.Start && addr < cached.End)
            return cached;

        var left = 0;
        var right = _vmas.Count - 1;
        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var vma = _vmas[mid];
            if (addr >= vma.Start && addr < vma.End)
            {
                _lastFaultVma = vma;
                return vma;
            }

            if (addr < vma.Start)
                right = mid - 1;
            else
                left = mid + 1;
        }

        return null;
    }

    public List<VmArea> FindVmAreasInRange(uint start, uint end)
    {
        var result = new List<VmArea>();
        VisitVmAreasInRange(start, end, result.Add);
        return result;
    }

    public void VisitVmAreasInRange(uint start, uint end, Action<VmArea> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (start >= end) return;

        var left = 0;
        var right = _vmas.Count - 1;
        var firstMatch = _vmas.Count;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            if (_vmas[mid].End > start)
            {
                firstMatch = mid;
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        for (var i = firstMatch; i < _vmas.Count && _vmas[i].Start < end; i++)
            visitor(_vmas[i]);
    }

    private bool TryGetVmAreaWindow(uint start, uint end, out int startIndex, out int endIndexExclusive)
    {
        startIndex = 0;
        endIndexExclusive = 0;
        if (start >= end || _vmas.Count == 0)
            return false;

        var left = 0;
        var right = _vmas.Count - 1;
        var firstMatch = _vmas.Count;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            if (_vmas[mid].End > start)
            {
                firstMatch = mid;
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        if (firstMatch == _vmas.Count || _vmas[firstMatch].Start >= end)
            return false;

        var lastExclusive = firstMatch;
        while (lastExclusive < _vmas.Count && _vmas[lastExclusive].Start < end)
            lastExclusive++;

        startIndex = firstMatch;
        endIndexExclusive = lastExclusive;
        return true;
    }

    private void ReplaceVmaWindow(int startIndex, int removeCount, IReadOnlyList<VmArea> replacements)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(removeCount);
        ArgumentNullException.ThrowIfNull(replacements);

        var commonCount = Math.Min(removeCount, replacements.Count);
        for (var i = 0; i < commonCount; i++)
            _vmas[startIndex + i] = replacements[i];

        var removeTailCount = removeCount - commonCount;
        if (removeTailCount > 0)
            _vmas.RemoveRange(startIndex + commonCount, removeTailCount);

        var insertTailCount = replacements.Count - commonCount;
        if (insertTailCount > 0)
        {
            var tail = new List<VmArea>(insertTailCount);
            for (var i = commonCount; i < replacements.Count; i++)
                tail.Add(replacements[i]);
            _vmas.InsertRange(startIndex + commonCount, tail);
        }
    }

    private static uint ComputeRangeEnd(uint addr, uint len)
    {
        var end = unchecked(addr + len);
        return end < addr ? uint.MaxValue : end;
    }

    private static (ulong Start, ulong EndExclusive) ComputePageAlignedRange(uint addr, uint len)
    {
        if (len == 0) return (0, 0);
        var start = (ulong)(addr & LinuxConstants.PageMask);
        var rawEndExclusive = (ulong)addr + len;
        var maxExclusive = (ulong)uint.MaxValue + 1;
        if (rawEndExclusive > maxExclusive) rawEndExclusive = maxExclusive;

        var endExclusive = (rawEndExclusive + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (endExclusive > maxExclusive) endExclusive = maxExclusive;
        return (start, endExclusive);
    }

    [Conditional("DEBUG")]
    private static void DebugAssert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [Conditional("DEBUG")]
    private void AssertExternalPagesReleasedForRange(uint addr, uint len, string source)
    {
        if (len == 0) return;
        var (start, endExclusive) = ComputePageAlignedRange(addr, len);
        for (var page = start; page < endExclusive; page += LinuxConstants.PageSize)
            if (PageMapping.TryGet((uint)page, out var ptr))
                throw new InvalidOperationException(
                    $"{source}: stale external page mapping remains at 0x{page:x8}, ptr=0x{ptr.ToInt64():x}");
    }

    private static void MergeRangesInPlace(List<NativeRange> ranges)
    {
        if (ranges.Count <= 1) return;
        ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var writeIndex = 0;
        for (var readIndex = 1; readIndex < ranges.Count; readIndex++)
        {
            var current = ranges[readIndex];
            var last = ranges[writeIndex];
            var lastEnd = (ulong)last.Start + last.Length;
            if (current.Start <= lastEnd)
            {
                var currentEnd = (ulong)current.Start + current.Length;
                var mergedEnd = Math.Max(lastEnd, currentEnd);
                ranges[writeIndex] = new NativeRange(last.Start, (uint)(mergedEnd - last.Start));
                continue;
            }

            writeIndex++;
            ranges[writeIndex] = current;
        }

        if (writeIndex + 1 < ranges.Count)
            ranges.RemoveRange(writeIndex + 1, ranges.Count - (writeIndex + 1));
    }

    public void TearDownNativeMappings(Engine engine, uint addr, uint len, bool captureDirtySharedPages,
        bool invalidateCodeRange, bool releaseExternalPages, bool preserveOwnerBinding = false)
    {
        if (len == 0) return;
        DebugAssert(releaseExternalPages,
            $"TearDownNativeMappings must release external pages whenever MemUnmap is performed. addr=0x{addr:x8} len={len}");
        if (captureDirtySharedPages)
        {
            var end = ComputeRangeEnd(addr, len);
            CaptureDirtySharedPages(engine, addr, end);
        }

        if (invalidateCodeRange)
            engine.ResetCodeCacheByRange(addr, len);
        engine.MemUnmap(addr, len);
        PageMapping.ReleaseRange(addr, len, preserveOwnerBinding);
        AssertExternalPagesReleasedForRange(addr, len, "TearDownNativeMappings");
    }

    public void ReprotectNativeMappings(Engine engine, uint addr, uint len, Protection perms,
        bool resetCodeCacheRange)
    {
        if (len == 0) return;
        engine.ReprotectMappedRange(addr, len, (byte)perms);
        if (resetCodeCacheRange)
            engine.ResetCodeCacheByRange(addr, len);
    }

    public void RebuildExternalMappingsFromNative(Engine engine, IEnumerable<VmArea> vmas)
    {
        const int MaxPagesPerChunk = 256;
        var buffer = new X86Native.PageMapping[MaxPagesPerChunk];

        foreach (var vma in vmas)
        {
            if (vma.Length == 0) continue;

            for (var cursor = vma.Start; cursor < vma.End;)
            {
                var chunkLen = Math.Min(vma.End - cursor, MaxPagesPerChunk * LinuxConstants.PageSize);
                var count = engine.CollectMappedPages(cursor, chunkLen, buffer);
                for (var i = 0; i < count; i++)
                {
                    var mapping = buffer[i];
                    if ((mapping.Flags & X86Native.PageMappingFlags.External) == 0) continue;
                    var pageIndex = GetVmaPageIndex(vma, mapping.GuestPage);
                    var binding = CreateResolvedPageBinding(vma, pageIndex, mapping.HostPage);
                    PageMapping.AddBinding(mapping.GuestPage, binding, out _);
                }

                cursor += chunkLen;
            }
        }
    }

    private List<NativeRange> ApplyFileTruncateMetadata(Inode inode, long newSize)
    {
        if (newSize < 0) newSize = 0;

        var ranges = new List<NativeRange>();
        var touchedVmMappings = new HashSet<AddressSpace>();
        var touchedVmAnonVmas = new HashSet<AnonVma>();
        foreach (var vma in _vmas)
        {
            if (!ReferenceEquals(vma.File?.OpenedInode, inode)) continue;

            var validBytes = newSize > vma.Offset ? newSize - vma.Offset : 0;
            if (validBytes > vma.Length) validBytes = vma.Length;
            if (TryGetVmMapping(vma, out var mapping))
                touchedVmMappings.Add(mapping);
            if (vma.VmAnonVma != null)
                touchedVmAnonVmas.Add(vma.VmAnonVma);

            if (validBytes >= vma.Length) continue;

            var tearDownFrom = validBytes <= 0
                ? vma.Start
                : vma.Start + (uint)((validBytes + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize *
                                     LinuxConstants.PageSize);
            if (tearDownFrom < vma.End)
                ranges.Add(new NativeRange(tearDownFrom, vma.End - tearDownFrom));
        }

        foreach (var sharedObject in touchedVmMappings)
            sharedObject.TruncateToSize(newSize);
        foreach (var privateObject in touchedVmAnonVmas)
            privateObject.TruncateToSize(newSize);

        MergeRangesInPlace(ranges);
        return ranges;
    }

    private List<NativeRange> CollectExecutableFileContentRanges(Inode inode, long start, long len)
    {
        var ranges = new List<NativeRange>();
        if (len <= 0) return ranges;

        long end;
        try
        {
            end = checked(start + len);
        }
        catch (OverflowException)
        {
            end = long.MaxValue;
        }

        foreach (var vma in _vmas)
        {
            if ((vma.Perms & Protection.Exec) == 0) continue;
            if (!ReferenceEquals(ResolveCurrentFileBacking(vma.File), inode)) continue;

            var mappingStart = vma.Offset;
            long mappingEnd;
            try
            {
                mappingEnd = checked(vma.Offset + (long)vma.Length);
            }
            catch (OverflowException)
            {
                mappingEnd = long.MaxValue;
            }

            var overlapStart = Math.Max(start, mappingStart);
            var overlapEnd = Math.Min(end, mappingEnd);
            if (overlapStart >= overlapEnd) continue;

            var guestStart = vma.Start + (uint)(overlapStart - mappingStart);
            var guestLen = (uint)(overlapEnd - overlapStart);
            ranges.Add(new NativeRange(guestStart, guestLen));
        }

        MergeRangesInPlace(ranges);
        return ranges;
    }

    public void NotifyFileContentChanged(Inode inode, long start, long len, IReadOnlyList<Engine> engines)
    {
        var ranges = CollectExecutableFileContentRanges(inode, start, len);
        if (ranges.Count == 0)
            return;

        var sequence = BumpMapSequence();
        foreach (var range in ranges)
            RecordCodeCacheResetRange(sequence, range.Start, range.Length);

        if (engines.Count == 0)
            return;

        foreach (var engine in engines)
        {
            foreach (var range in ranges)
                engine.ResetCodeCacheByRange(range.Start, range.Length);
            engine.AddressSpaceMapSequenceSeen = sequence;
        }
    }

    public void OnFileTruncate(Inode inode, long newSize, Engine engine)
    {
        OnFileTruncate(inode, newSize, [engine]);
    }

    public void OnFileTruncate(Inode inode, long newSize, IReadOnlyList<Engine> engines)
    {
        var ranges = ApplyFileTruncateMetadata(inode, newSize);
        if (ranges.Count == 0)
            return;

        var sequence = BumpMapSequence();
        foreach (var range in ranges)
            RecordCodeCacheResetRange(sequence, range.Start, range.Length);

        if (engines.Count == 0)
        {
            foreach (var range in ranges)
                AssertExternalPagesReleasedForRange(range.Start, range.Length, "OnFileTruncate.no-engines");
            return;
        }

        var primary = engines[0];
        foreach (var engine in engines)
            if (engine.CurrentMmuIdentityInternal != primary.CurrentMmuIdentityInternal)
                throw new InvalidOperationException(
                    "OnFileTruncate requires all engines in the same address space to share one MMU core.");

        foreach (var range in ranges)
            TearDownNativeMappings(
                primary,
                range.Start,
                range.Length,
                false,
                true,
                true);

        primary.AddressSpaceMapSequenceSeen = sequence;

        foreach (var range in ranges)
            AssertExternalPagesReleasedForRange(range.Start, range.Length, "OnFileTruncate");
    }

    public void UnmapMappingRange(Inode inode, long start, long len, bool evenCows, IReadOnlyList<Engine> engines)
    {
        if (len <= 0) return;
        long end;
        try
        {
            end = checked(start + len);
        }
        catch (OverflowException)
        {
            end = long.MaxValue;
        }

        var ranges = new List<NativeRange>();
        foreach (var vma in _vmas)
        {
            if (!ReferenceEquals(vma.File?.OpenedInode, inode)) continue;
            if (!evenCows && (vma.Flags & MapFlags.Private) != 0) continue;

            var mappingStart = vma.Offset;
            var mappingEnd = vma.Offset + vma.Length;
            var overlapStart = Math.Max(start, mappingStart);
            var overlapEnd = Math.Min(end, mappingEnd);
            if (overlapStart >= overlapEnd) continue;

            var guestStart = vma.Start + (uint)(overlapStart - mappingStart);
            var guestLen = (uint)(overlapEnd - overlapStart);
            ranges.Add(new NativeRange(guestStart, guestLen));
        }

        if (ranges.Count == 0) return;
        MergeRangesInPlace(ranges);
        var sequence = BumpMapSequence();
        foreach (var range in ranges)
            RecordCodeCacheResetRange(sequence, range.Start, range.Length);

        if (engines.Count > 0)
        {
            var primary = engines[0];
            foreach (var range in ranges)
                TearDownNativeMappings(primary, range.Start, range.Length, false, true, true);
            primary.AddressSpaceMapSequenceSeen = sequence;
        }
    }

    public uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, LinuxFile? file, long offset,
        string name, Engine engine)
    {
        return Mmap(addr, len, perms, flags, file, offset, name, engine, null);
    }

    internal uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, LinuxFile? file, long offset,
        string name, Engine engine, TbCohWorkSet? tbCohWorkSet)
    {
        // Align to 4k
        if ((addr & LinuxConstants.PageOffsetMask) != 0)
            throw new ArgumentException("Address not aligned");

        // Round up len to 4k
        len = (len + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (len == 0)
            throw new ArgumentException("Mapping length must be non-zero", nameof(len));

        var isFixedNoReplace = (flags & MapFlags.FixedNoReplace) != 0;
        var isFixed = (flags & MapFlags.Fixed) != 0 || isFixedNoReplace;

        if (addr == 0)
        {
            addr = FindFreeRegion(len);
            if (addr == 0)
                throw new OutOfMemoryException("Execution out of memory");
        }

        uint end;
        try
        {
            end = checked(addr + len);
        }
        catch (OverflowException)
        {
            throw new OutOfMemoryException("Mapping range overflow");
        }

        if (end > LinuxConstants.TaskSize32)
            throw new OutOfMemoryException("Mapping exceeds user task address space");

        if (CheckOverlap(addr, end))
        {
            if (isFixed)
                throw new InvalidOperationException("Overlap detected");

            // Linux treats a non-MAP_FIXED address as a hint. If the hinted range is busy,
            // the kernel is free to place the mapping elsewhere instead of failing.
            addr = FindFreeRegion(len);
            if (addr == 0)
                throw new OutOfMemoryException("Execution out of memory");

            end = checked(addr + len);
        }

        var isShared = (flags & MapFlags.Shared) != 0;
        var isPrivate = (flags & MapFlags.Private) != 0;
        AddressSpace? sharedObj = null;
        AnonVma? privateObj = null;
        ulong vmPgoff = 0;
        VmaFileMapping? fileMapping = null;

        LinuxFile? anonymousSharedFile = null;
        try
        {
            if (file == null)
            {
                if (isShared)
                {
                    // Linux models MAP_SHARED|MAP_ANONYMOUS with an internal shmem/tmpfs backing object.
                    anonymousSharedFile = MemoryContext.CreateSharedAnonymousMappingFile(len);
                    file = anonymousSharedFile;
                    sharedObj = ResolveCurrentFileMappingBacking(file)!.AcquireMappingRef();
                    fileMapping = new VmaFileMapping(file);
                }
                else if (isPrivate)
                {
                    sharedObj = MemoryContext.AcquireZeroMappingRef();
                }
            }
            else if (isShared)
            {
                // MAP_SHARED file: share the inode's global page cache
                sharedObj = ResolveCurrentFileMappingBacking(file)!.AcquireMappingRef();
                vmPgoff = (ulong)(offset / LinuxConstants.PageSize);
                fileMapping = new VmaFileMapping(file);
            }
            else
            {
                // MAP_PRIVATE file: clean reads come from inode mapping, private COW pages are created lazily.
                sharedObj = ResolveCurrentFileMappingBacking(file)!.AcquireMappingRef();
                vmPgoff = (ulong)(offset / LinuxConstants.PageSize);
                fileMapping = new VmaFileMapping(file);
            }

            var vma = new VmArea
            {
                Start = addr,
                End = end,
                Perms = perms,
                Flags = flags,
                FileMapping = fileMapping,
                Offset = offset,
                VmPgoff = vmPgoff,
                Name = name,
                VmMapping = sharedObj,
                VmAnonVma = privateObj
            };

            InsertVmaSorted(vma);
            RegisterVmAreaAttachments(vma, tbCohWorkSet);
            anonymousSharedFile = null;
            return addr;
        }
        catch
        {
            anonymousSharedFile?.Close();
            throw;
        }
    }

    public VMAManager Clone()
    {
        var newMM = new VMAManager(MemoryContext);
        foreach (var vma in _vmas)
        {
            var cloned = vma.Clone();
            newMM._vmas.Add(cloned);
            newMM.TrackMappedInodeOnVmaAdded(cloned);
            newMM.RegisterVmAreaAttachments(cloned);
        }

        foreach (var pageAddr in PageMapping.SnapshotMappedPages())
        {
            if (!PageMapping.TryGetBinding(pageAddr, out var binding) || binding == null) continue;

            var clonedVma = newMM.FindVmArea(pageAddr);
            if (clonedVma == null)
                throw new InvalidOperationException(
                    $"Clone external binding lost VMA context: page=0x{pageAddr:X8}, owner={binding.OwnerKind}");

            var pageIndex = clonedVma.GetPageIndex(pageAddr);
            MappedPageBinding clonedBinding;
            if (binding.OwnerKind == MappedPageOwnerKind.AnonVma &&
                clonedVma.VmAnonVma?.PeekVmPage(pageIndex) is { } anonPage)
                clonedBinding = MappedPageBinding.FromAnonVmaPage(clonedVma.VmAnonVma, pageIndex, anonPage);
            else if (binding.OwnerKind == MappedPageOwnerKind.AddressSpace &&
                     clonedVma.VmMapping?.PeekVmPage(pageIndex) is { } sharedPage)
                clonedBinding = MappedPageBinding.FromAddressSpacePage(clonedVma.VmMapping, pageIndex, sharedPage);
            else
                throw new InvalidOperationException(
                    $"Clone encountered unmanaged page binding owner={binding.OwnerKind} page=0x{pageAddr:X8}.");

            _ = newMM.PageMapping.AddBinding(pageAddr, clonedBinding, out _);
        }

        return newMM;
    }

    /// <summary>
    ///     Add a pre-constructed VmArea directly. Used by SysV SHM subsystem.
    /// </summary>
    internal void AddVmaInternal(VmArea vma)
    {
        InsertVmaSorted(vma);
        RegisterVmAreaAttachments(vma);
    }

    public void Munmap(uint addr, uint length, Engine engine)
    {
        if (length == 0) return;
        var unmappedHostPages = new HashSet<IntPtr>();
        List<(AnonVma AnonVma, uint StartPageIndex, uint EndPageIndexExclusive)>? pendingObjectPageReleases = null;
        uint end;
        try
        {
            end = checked(addr + length);
        }
        catch (OverflowException)
        {
            return;
        }

        if (TryGetVmAreaWindow(addr, end, out var startIndex, out var endIndexExclusive))
        {
            var replacements = new List<VmArea>(endIndexExclusive - startIndex + 1);
            List<VmArea>? removedVmas = null;
            List<VmArea>? addedVmas = null;
            List<VmArea>? registerAfter = null;
            List<VmArea>? releaseAfter = null;

            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = _vmas[i];
                DebugAssert(end > vma.Start && addr < vma.End,
                    $"TryGetVmAreaWindow returned non-overlapping VMA start=0x{vma.Start:X8} end=0x{vma.End:X8}.");

                // Full removal
                if (addr <= vma.Start && end >= vma.End)
                {
                    SyncVmArea(vma, engine, vma.Start, vma.End);
                    CollectHostPagesForVmaRange(vma, vma.Start, vma.End, unmappedHostPages);
                    QueueUnmappedObjectPages(vma, vma.Start, vma.End, pendingObjectPageReleases ??= []);
                    UnregisterVmAreaAttachments(vma);
                    if (ReferenceEquals(_lastFaultVma, vma)) _lastFaultVma = null;
                    (removedVmas ??= []).Add(vma);
                    (releaseAfter ??= []).Add(vma);
                    continue;
                }

                // Split (Middle removal)
                if (addr > vma.Start && end < vma.End)
                {
                    SyncVmArea(vma, engine, addr, end);
                    CollectHostPagesForVmaRange(vma, addr, end, unmappedHostPages);
                    QueueUnmappedObjectPages(vma, addr, end, pendingObjectPageReleases ??= []);
                    UnregisterVmAreaAttachments(vma);
                    var tailStart = end;
                    var tailEnd = vma.End;
                    long tailOffset = 0;
                    if (vma.File != null)
                    {
                        long diff = tailStart - vma.Start;
                        tailOffset = vma.Offset + diff;
                    }

                    var tailVmArea = new VmArea
                    {
                        Start = tailStart,
                        End = tailEnd,
                        Perms = vma.Perms,
                        Flags = vma.Flags,
                        FileMapping = vma.FileMapping?.AddRef(),
                        Offset = tailOffset,
                        Name = vma.Name,
                        VmMapping = vma.VmMapping,
                        VmAnonVma = ShareVmAnonVmaForSplit(vma),
                        VmPgoff = vma.GetPageIndex(tailStart)
                    };
                    vma.VmMapping?.AddRef();

                    vma.End = addr;
                    replacements.Add(vma);
                    replacements.Add(tailVmArea);
                    (addedVmas ??= []).Add(tailVmArea);
                    (registerAfter ??= []).Add(vma);
                    registerAfter.Add(tailVmArea);
                    continue;
                }

                // Head removal
                if (addr <= vma.Start && end < vma.End)
                {
                    SyncVmArea(vma, engine, vma.Start, end);
                    CollectHostPagesForVmaRange(vma, vma.Start, end, unmappedHostPages);
                    QueueUnmappedObjectPages(vma, vma.Start, end, pendingObjectPageReleases ??= []);
                    UnregisterVmAreaAttachments(vma);
                    var diff = end - vma.Start;
                    vma.Start = end;
                    vma.VmPgoff = vma.GetPageIndex(end);
                    if (vma.File != null)
                        vma.Offset += diff;
                    replacements.Add(vma);
                    (registerAfter ??= []).Add(vma);
                    continue;
                }

                // Tail removal
                if (addr > vma.Start && end >= vma.End)
                {
                    SyncVmArea(vma, engine, addr, vma.End);
                    CollectHostPagesForVmaRange(vma, addr, vma.End, unmappedHostPages);
                    QueueUnmappedObjectPages(vma, addr, vma.End, pendingObjectPageReleases ??= []);
                    UnregisterVmAreaAttachments(vma);
                    vma.End = addr;
                    replacements.Add(vma);
                    (registerAfter ??= []).Add(vma);
                }
            }

            ReplaceVmaWindow(startIndex, endIndexExclusive - startIndex, replacements);

            if (removedVmas != null)
                foreach (var vma in removedVmas)
                    TrackMappedInodeOnVmaRemoved(vma);

            if (addedVmas != null)
                foreach (var vma in addedVmas)
                    TrackMappedInodeOnVmaAdded(vma);

            if (registerAfter != null)
                foreach (var vma in registerAfter)
                    RegisterVmAreaAttachments(vma);

            if (releaseAfter != null)
                foreach (var vma in releaseAfter)
                {
                    vma.VmMapping?.Release();
                    vma.VmAnonVma?.Release();
                    vma.FileMapping?.Release();
                }
        }

        var nativeMappingsTornDown = false;
        try
        {
            TearDownNativeMappings(
                engine,
                addr,
                length,
                false,
                true,
                true,
                preserveOwnerBinding: pendingObjectPageReleases != null);
            nativeMappingsTornDown = true;
        }
        finally
        {
            ReleasePendingObjectPages(pendingObjectPageReleases, nativeMappingsTornDown);
        }

        ClearTbWpRange(addr, length);
        foreach (var hostPagePtr in unmappedHostPages)
            TbCoh.ApplyWx(MemoryContext, hostPagePtr);
    }

    public int Mprotect(uint addr, uint len, Protection prot, Engine engine, out bool resetCodeCacheRange)
    {
        return Mprotect(addr, len, prot, engine, out resetCodeCacheRange, null);
    }

    internal int Mprotect(uint addr, uint len, Protection prot, Engine engine, out bool resetCodeCacheRange,
        TbCohWorkSet? tbCohWorkSet)
    {
        resetCodeCacheRange = false;
        if (len == 0) return 0;

        uint end;
        try
        {
            end = checked(addr + len);
        }
        catch (OverflowException)
        {
            return -(int)Errno.ENOMEM;
        }

        var vmas = FindVmAreasInRange(addr, end);
        if (vmas.Count == 0) return -(int)Errno.ENOMEM;

        vmas.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = addr;
        foreach (var vma in vmas)
        {
            if (vma.Start > cursor) return -(int)Errno.ENOMEM;
            if (vma.End > cursor) cursor = vma.End;
            if (cursor >= end) break;
        }

        if (cursor < end) return -(int)Errno.ENOMEM;

        if (TryGetVmAreaWindow(addr, end, out var startIndex, out var endIndexExclusive))
        {
            var replacements = new List<VmArea>(endIndexExclusive - startIndex + 2);
            List<VmArea>? addedVmas = null;

            for (var i = startIndex; i < endIndexExclusive; i++)
            {
                var vma = _vmas[i];
                var overlapStart = Math.Max(vma.Start, addr);
                var overlapEnd = Math.Min(vma.End, end);
                if (overlapStart >= overlapEnd)
                    continue;

                var oldStart = vma.Start;
                var oldEnd = vma.End;
                var oldPerms = vma.Perms;
                var oldOffset = vma.Offset;
                resetCodeCacheRange |= ((oldPerms ^ prot) & Protection.Exec) != 0;

                // Fully covered: just flip perms.
                if (overlapStart == oldStart && overlapEnd == oldEnd)
                {
                    UpdateTbCohRolesForVmaRange(vma, overlapStart, overlapEnd, oldPerms, prot, tbCohWorkSet);
                    vma.Perms = prot;
                    replacements.Add(vma);
                    continue;
                }

                // Prepare middle (the protected slice).
                var midDiff = (long)overlapStart - oldStart;
                var originalFileMapping = vma.FileMapping;
                var mid = new VmArea
                {
                    Start = overlapStart,
                    End = overlapEnd,
                    Perms = prot,
                    Flags = vma.Flags,
                    FileMapping = null,
                    Offset = vma.File != null ? oldOffset + midDiff : 0,
                    Name = vma.Name,
                    VmMapping = vma.VmMapping,
                    VmAnonVma = ShareVmAnonVmaForSplit(vma),
                    VmPgoff = vma.GetPageIndex(overlapStart)
                };
                vma.VmMapping?.AddRef();

                // Left tail stays in the existing VmArea.
                var hasLeft = overlapStart > oldStart;
                var hasRight = overlapEnd < oldEnd;
                mid.FileMapping = hasLeft ? originalFileMapping?.AddRef() : originalFileMapping;

                if (hasLeft)
                {
                    vma.Start = oldStart;
                    vma.End = overlapStart;
                    vma.Perms = oldPerms;
                }

                // Right tail (if any) keeps old perms.
                VmArea? right = null;
                if (hasRight)
                {
                    var rightDiff = (long)overlapEnd - oldStart;
                    right = new VmArea
                    {
                        Start = overlapEnd,
                        End = oldEnd,
                        Perms = oldPerms,
                        Flags = vma.Flags,
                        FileMapping = originalFileMapping?.AddRef(),
                        Offset = vma.File != null ? oldOffset + rightDiff : 0,
                        Name = vma.Name,
                        VmMapping = vma.VmMapping,
                        VmAnonVma = ShareVmAnonVmaForSplit(vma),
                        VmPgoff = vma.GetPageIndex(overlapEnd)
                    };
                    vma.VmMapping?.AddRef();
                }

                if (!hasLeft)
                {
                    // Reuse current slot for middle when there is no left tail.
                    // Move VmAnonVma ownership to reused VmArea slot; otherwise mid.VmAnonVma is leaked.
                    var oldPrivate = vma.VmAnonVma;
                    vma.Start = mid.Start;
                    vma.End = mid.End;
                    vma.Perms = mid.Perms;
                    vma.FileMapping = mid.FileMapping;
                    vma.Offset = mid.Offset;
                    vma.Name = mid.Name;
                    vma.VmPgoff = mid.VmPgoff;
                    vma.VmMapping = mid.VmMapping;
                    vma.VmAnonVma = mid.VmAnonVma;
                    mid.VmAnonVma = null;
                    mid.FileMapping = null;
                    // Drop the temporary extra ref held by mid.
                    mid.VmMapping?.Release();
                    oldPrivate?.Release();

                    replacements.Add(vma);
                    if (right != null)
                    {
                        replacements.Add(right);
                        (addedVmas ??= []).Add(right);
                    }
                }
                else
                {
                    // Keep left in current slot; insert middle and optional right.
                    replacements.Add(vma);
                    replacements.Add(mid);
                    (addedVmas ??= []).Add(mid);
                    if (right != null)
                    {
                        replacements.Add(right);
                        addedVmas.Add(right);
                    }
                }

                if (hasLeft)
                    RebindRmapRefsForVmaRange(vma, mid, overlapStart, overlapEnd, oldPerms, prot, tbCohWorkSet);
                else
                    UpdateTbCohRolesForVmaRange(vma, overlapStart, overlapEnd, oldPerms, prot, tbCohWorkSet);

                if (right != null)
                    RebindRmapRefsForVmaRange(vma, right, overlapEnd, oldEnd, oldPerms, oldPerms, tbCohWorkSet);

                ResetVmAreaAttachmentsForSplit(vma, hasLeft ? mid : right, hasLeft ? right : null);
            }

            ReplaceVmaWindow(startIndex, endIndexExclusive - startIndex, replacements);

            if (addedVmas != null)
                foreach (var vma in addedVmas)
                    TrackMappedInodeOnVmaAdded(vma);
        }

        if (prot == Protection.None)
            TearDownNativeMappings(
                engine,
                addr,
                len,
                true,
                resetCodeCacheRange,
                true,
                true);
        else
            ReprotectNativeMappings(engine, addr, len, prot, resetCodeCacheRange);

        return 0;
    }

    public void Clear(Engine engine)
    {
        foreach (var vma in _vmas)
        {
            SyncVmArea(vma, engine);
            // Clear native mappings through unified teardown path.
            TearDownNativeMappings(
                engine,
                vma.Start,
                vma.End - vma.Start,
                false,
                true,
                true);
            UnregisterVmAreaAttachments(vma);
            vma.VmMapping?.Release();
            vma.VmAnonVma?.Release();
            vma.FileMapping?.Release();
        }

        _lastFaultVma = null;
        foreach (var vma in _vmas)
            TrackMappedInodeOnVmaRemoved(vma);

        _mappedInodeRefCounts.Clear();
        _vmas.Clear();
    }

    private bool CheckOverlap(uint start, uint end)
    {
        foreach (var vma in _vmas)
            if (start < vma.End && end > vma.Start)
                return true;
        return false;
    }

    public void EagerMap(uint addr, uint len, Engine engine)
    {
        var startPage = addr & LinuxConstants.PageMask;
        var endAddr = addr + len;
        for (var p = startPage; p < endAddr; p += LinuxConstants.PageSize) HandleFault(p, true, engine);
    }

    internal uint FindFreeRegion(uint size)
    {
        var baseAddr = LinuxConstants.MinMmapAddr;

        // Optimize: Because _vmas is sorted by Start, we can just walk _vmas 
        // and jump baseAddr to vma.End whenever there's a collision.
        foreach (var vma in _vmas)
        {
            uint endAddr;
            try
            {
                endAddr = checked(baseAddr + size);
            }
            catch (OverflowException)
            {
                return 0; // Out of memory space
            }

            if (endAddr > LinuxConstants.TaskSize32)
                return 0;

            if (baseAddr < vma.End && endAddr > vma.Start)
                // Collision! We can't put it here.
                // The next possible spot is right after this colliding VmArea.
                baseAddr = (vma.End + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask; // Ensure page alignment
        }

        // Final check against absolute top limit after loop
        uint finalEnd;
        try
        {
            finalEnd = checked(baseAddr + size);
        }
        catch (OverflowException)
        {
            return 0;
        }

        if (finalEnd > LinuxConstants.TaskSize32)
            return 0;

        return baseAddr;
    }

    internal int DropMappedCleanRecoverablePagesForPressure(Engine engine, int maxPages)
    {
        if (maxPages <= 0) return 0;

        var pages = PageMapping.SnapshotMappedPages();
        if (pages.Count == 0) return 0;

        var sorted = pages.ToArray();
        Array.Sort(sorted);

        var dropped = 0;
        foreach (var pageAddr in sorted)
        {
            if (dropped >= maxPages) break;
            if (!PageMapping.TryGet(pageAddr, out _)) continue;

            var vma = FindVmArea(pageAddr);
            if (vma == null) continue;

            var pageIndex = vma.GetPageIndex(pageAddr);
            if (engine.IsDirty(pageAddr)) continue;
            if (vma.VmAnonVma != null && vma.VmAnonVma.PeekPage(pageIndex) != IntPtr.Zero) continue;
            if (IsAnonymousPrivateZeroSource(vma))
            {
                if (!PageMapping.TryGetBinding(pageAddr, out var binding) ||
                    binding == null ||
                    binding.OwnerKind != MappedPageOwnerKind.AddressSpace ||
                    !ReferenceEquals(binding.Mapping, vma.VmMapping))
                    continue;
            }
            else
            {
                var mapping = vma.VmMapping;
                if (mapping == null || !mapping.IsRecoverableWithoutSwap) continue;
                if (mapping.IsDirty(pageIndex)) continue;
            }

            TearDownNativeMappings(
                engine,
                pageAddr,
                LinuxConstants.PageSize,
                false,
                true,
                true);
            dropped++;
        }

        return dropped;
    }

    private bool TryRelieveFaultMemoryPressure(Engine engine, uint faultAddr, string source)
    {
        const int TargetPages = 1024; // 4 MiB
        var result = MemoryContext.MemoryPressure.TryRelieveFault(
            this,
            engine,
            (long)LinuxConstants.PageSize * TargetPages,
            TargetPages);
        if (result.MadeProgress)
        {
            Logger.LogDebug(
                "[FaultPressure] source={Source} fault=0x{FaultAddr:x} unmappedPages={UnmappedPages} reclaimedBytes={ReclaimedBytes}",
                source, faultAddr, result.UnmappedPages, result.ReclaimedBytes);
            return true;
        }

        return false;
    }

    private static bool IsPrivateVma(VmArea vma)
    {
        return (vma.Flags & MapFlags.Private) != 0;
    }

    private static bool IsAnonymousPrivateZeroSource(VmArea vma)
    {
        return !vma.IsFileBacked && vma.VmMapping is { IsZeroBacking: true };
    }

    private static bool TryGetVmMapping(VmArea vma, out AddressSpace mapping)
    {
        mapping = vma.VmMapping!;
        return mapping != null;
    }

    private static AddressSpace RequireVmMapping(VmArea vma)
    {
        return vma.VmMapping ?? throw new InvalidOperationException("VmArea requires an address_space mapping.");
    }

    public void CaptureDirtyPrivatePages(Engine engine)
    {
        foreach (var vma in _vmas)
            CaptureDirtyPrivatePages(engine, vma.Start, vma.End);
    }

    public void CaptureDirtyPrivatePages(Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (rangeStart >= rangeEnd) return;
        VisitVmAreasInRange(rangeStart, rangeEnd, vma =>
        {
            if (!IsPrivateVma(vma)) return;
            var captureStart = Math.Max(vma.Start, rangeStart) & LinuxConstants.PageMask;
            var captureEnd = (Math.Min(vma.End, rangeEnd) + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
            for (var page = captureStart; page < captureEnd; page += LinuxConstants.PageSize)
            {
                if (!engine.IsDirty(page)) continue;
                if (!PageMapping.TryGet(page, out var mappedPtr)) continue;
                var pageIndex = vma.GetPageIndex(page);
                var privatePtr = vma.VmAnonVma!.PeekPage(pageIndex);
                if (privatePtr == IntPtr.Zero || privatePtr != mappedPtr) continue;
                vma.VmAnonVma.MarkDirty(pageIndex);
            }
        });
    }

    private bool TryAllocatePrivateCopyFromSource(
        Engine engine,
        uint pageStart,
        IntPtr sourcePage,
        string pressureSource,
        out BackingPageHandle privatePage)
    {
        privatePage = default;
        if (!MemoryContext.BackingPagePool.TryAllocAnonPageMayFail(out privatePage, AllocationClass.Cow,
                AllocationSource.CowFirstPrivate))
            if (!TryRelieveFaultMemoryPressure(engine, pageStart, pressureSource) ||
                !MemoryContext.BackingPagePool.TryAllocAnonPageMayFail(out privatePage, AllocationClass.Cow,
                    AllocationSource.CowFirstPrivate))
                return false;

        var privatePagePtr = privatePage.Pointer;
        unsafe
        {
            Buffer.MemoryCopy((void*)sourcePage, (void*)privatePagePtr,
                LinuxConstants.PageSize, LinuxConstants.PageSize);
        }

        return true;
    }

    private static int GetPageReadLength(VmArea vma, long vmaRelativeOffset)
    {
        return vma.GetReadLengthForRelativeOffset(vmaRelativeOffset);
    }

    private static bool WantsWritableMappedFilePage(VmArea vma)
    {
        return vma.IsFileBacked &&
               (vma.Flags & MapFlags.Shared) != 0 &&
               (vma.Perms & Protection.Write) != 0;
    }

    private static bool TryPopulateAnonymousAddressSpacePage(IntPtr ptr)
    {
        unsafe
        {
            new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
        }

        return true;
    }

    private FaultResult ResolveAnonymousSharedAddressSpacePage(
        VmArea vma,
        uint pageIndex,
        long vmaRelativeOffset,
        Engine engine,
        out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        if (!TryGetVmMapping(vma, out var mapping))
            return FaultResult.Segv;

        pagePtr = mapping.GetOrCreatePage(pageIndex, static ptr => TryPopulateAnonymousAddressSpacePage(ptr), out _,
            true, AllocationClass.Anonymous, AllocationSource.AnonFault);
        if (pagePtr != IntPtr.Zero)
            return FaultResult.Handled;

        if (!TryRelieveFaultMemoryPressure(engine, (uint)(vma.Start + vmaRelativeOffset), "SharedBackingFault"))
            return FaultResult.Oom;

        pagePtr = mapping.GetOrCreatePage(pageIndex, static ptr => TryPopulateAnonymousAddressSpacePage(ptr), out _,
            true, AllocationClass.Anonymous, AllocationSource.AnonFault);
        return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Oom;
    }

    private FaultResult ResolveFileBackedSharedPage(
        VmArea vma,
        uint pageIndex,
        long vmaRelativeOffset,
        long absoluteFileOffset,
        bool preferWritableMappedPage,
        out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        if (vmaRelativeOffset >= vma.GetFileBackingLength())
            return FaultResult.BusError;

        if (preferWritableMappedPage && vma.File?.OpenedInode is OverlayInode overlayInode)
        {
            var copyRc = overlayInode.CopyUp(null);
            if (copyRc < 0)
                return FaultResult.Segv;
        }

        var inode = ResolveCurrentFileMappingBacking(vma.File);
        if (inode == null)
            return FaultResult.Segv;

        if (!TryGetVmMapping(vma, out var mapping))
            return FaultResult.Segv;

        var readLen = (int)Math.Min(LinuxConstants.PageSize, vma.GetFileBackingLength() - vmaRelativeOffset);
        pagePtr = inode.AcquireMappingPage(vma.File, pageIndex, absoluteFileOffset,
            preferWritableMappedPage ? PageCacheAccessMode.Write : PageCacheAccessMode.Read, readLen);
        return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Segv;
    }

    private FaultResult ResolveSharedBackingPage(
        VmArea vma,
        uint pageIndex,
        long vmaRelativeOffset,
        long absoluteFileOffset,
        bool preferWritableMappedPage,
        Engine engine,
        out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        if (IsAnonymousPrivateZeroSource(vma))
        {
            var mapping = RequireVmMapping(vma);
            if (!ReferenceEquals(engine.MemoryContext, MemoryContext))
                throw new InvalidOperationException("Engine memory context does not match VMAManager memory context.");
            if (!MemoryContext.IsZeroAddressSpace(mapping))
                return FaultResult.Segv;

            pagePtr = MemoryContext.AcquireZeroMappingPage(pageIndex);
            return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Oom;
        }

        if (vma.IsFileBacked)
            return ResolveFileBackedSharedPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
                preferWritableMappedPage, out pagePtr);

        return ResolveAnonymousSharedAddressSpacePage(vma, pageIndex, vmaRelativeOffset, engine, out pagePtr);
    }

    private FaultResult MapResolvedBackingPage(
        VmArea vma,
        uint pageStart,
        uint pageIndex,
        IntPtr pagePtr,
        byte perms,
        Engine engine)
    {
        var binding = CreateResolvedPageBinding(vma, pageIndex, pagePtr);
        var result = EnsureExternalMapping(pageStart, binding, perms, engine);
        if (result == FaultResult.Handled && (vma.Perms & Protection.Exec) != 0)
            TbCoh.ApplyWx(MemoryContext, binding.Ptr);
        return result;
    }

    private FaultResult ResolveAndMapSharedBackingPage(
        VmArea vma,
        uint pageStart,
        uint pageIndex,
        long vmaRelativeOffset,
        long absoluteFileOffset,
        bool preferWritableMappedPage,
        byte perms,
        Engine engine)
    {
        var sharedSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
            preferWritableMappedPage, engine, out var pagePtr);
        if (sharedSourceResult != FaultResult.Handled)
            return sharedSourceResult;

        return MapResolvedBackingPage(vma, pageStart, pageIndex, pagePtr, perms, engine);
    }

    private FaultResult InstallPrivatePageAndMap(
        AnonVma anonVma,
        uint pageStart,
        uint pageIndex,
        ref BackingPageHandle backingHandle,
        byte perms,
        Engine engine,
        bool markDirty)
    {
        anonVma.SetPage(pageIndex, ref backingHandle);
        if (markDirty)
            anonVma.MarkDirty(pageIndex);
        UnmarkTbWp(pageStart);
        return EnsureExternalMapping(pageStart,
            MappedPageBinding.FromAnonVmaPage(anonVma, pageIndex, anonVma.PeekVmPage(pageIndex)!),
            perms, engine);
    }

    private FaultResult EnsureExternalMapping(uint pageStart, MappedPageBinding binding, byte perms, Engine engine)
    {
        var pagePtr = binding.Ptr;
        var hasCurrent = PageMapping.TryGet(pageStart, out var mappedPtr);
        if (hasCurrent && mappedPtr == pagePtr)
        {
            engine.MemMap(pageStart, LinuxConstants.PageSize, perms);
            return FaultResult.Handled;
        }

        if (hasCurrent)
            PageMapping.Release(pageStart);

        if (!PageMapping.AddBinding(pageStart, binding, out var addedRef))
            return FaultResult.Segv;
        if (!engine.MapManagedPage(pageStart, pagePtr, perms))
        {
            if (addedRef) PageMapping.Release(pageStart);
            return FaultResult.Segv;
        }

        return FaultResult.Handled;
    }

    private FaultResult EnsureWritableExistingPrivatePage(
        AnonVma privateObject,
        uint pageStart,
        uint pageIndex,
        IntPtr existingPrivate,
        byte perms,
        Engine engine)
    {
        var ownerResidentCount = MemoryContext.HostPages.GetRequired(existingPrivate).OwnerResidentCount;
        if (ownerResidentCount <= 1)
        {
            privateObject.MarkDirty(pageIndex);
            TbCoh.OnWriteFault(this, pageStart, privateObject.PeekVmPage(pageIndex)!.Ptr);
            return EnsureExternalMapping(pageStart,
                MappedPageBinding.FromAnonVmaPage(privateObject, pageIndex, privateObject.PeekVmPage(pageIndex)!),
                perms, engine);
        }

        if (!MemoryContext.BackingPagePool.TryAllocAnonPageMayFail(out var replacementPage, AllocationClass.Cow,
                AllocationSource.CowReplacePrivate))
            if (!TryRelieveFaultMemoryPressure(engine, pageStart, "CowReplacePrivate") ||
                !MemoryContext.BackingPagePool.TryAllocAnonPageMayFail(out replacementPage, AllocationClass.Cow,
                    AllocationSource.CowReplacePrivate))
                return FaultResult.Oom;

        Interlocked.Increment(ref _cowAllocReplaceCount);
        var replacementPagePtr = replacementPage.Pointer;
        unsafe
        {
            Buffer.MemoryCopy((void*)existingPrivate, (void*)replacementPagePtr,
                LinuxConstants.PageSize, LinuxConstants.PageSize);
        }

        return InstallPrivatePageAndMap(privateObject, pageStart, pageIndex, ref replacementPage, perms, engine,
            true);
    }

    private FaultResult ResolveSharedMappingFault(
        VmArea vma,
        uint addr,
        uint pageStart,
        uint pageIndex,
        bool isWrite,
        Engine engine)
    {
        if (isWrite && (vma.Perms & Protection.Write) == 0)
        {
            Logger.LogTrace("Write fault on read-only VMA: {VmaName} at 0x{Addr:x}", vma.Name, addr);
            return FaultResult.Segv;
        }

        var vmaRelativeOffset = vma.GetRelativeOffsetForPageIndex(pageIndex);
        var absoluteFileOffset = vma.GetAbsoluteFileOffsetForPageIndex(pageIndex);
        var tempPerms = vma.Perms | Protection.Write;
        if (isWrite)
        {
            var writeSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
                WantsWritableMappedFilePage(vma), engine, out var writePagePtr);
            if (writeSourceResult != FaultResult.Handled)
                return writeSourceResult;

            TbCoh.OnWriteFault(this, pageStart, CreateResolvedPageBinding(vma, pageIndex, writePagePtr).Ptr);
            var writeMapResult = MapResolvedBackingPage(vma, pageStart, pageIndex, writePagePtr, (byte)tempPerms,
                engine);
            if (writeMapResult != FaultResult.Handled)
                return writeMapResult;

            if (tempPerms != vma.Perms)
                engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);

            return FaultResult.Handled;
        }

        var mappingResult = ResolveAndMapSharedBackingPage(vma, pageStart, pageIndex, vmaRelativeOffset,
            absoluteFileOffset, WantsWritableMappedFilePage(vma), (byte)tempPerms, engine);
        if (mappingResult != FaultResult.Handled)
            return mappingResult;

        if (tempPerms != vma.Perms)
            engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);

        return FaultResult.Handled;
    }

    private FaultResult ResolvePrivateFault(VmArea vma, uint pageStart, uint pageIndex, bool isWrite, Engine engine)
    {
        var privateObject = vma.VmAnonVma;
        var vmaRelativeOffset = vma.GetRelativeOffsetForPageIndex(pageIndex);
        var absoluteFileOffset = vma.GetAbsoluteFileOffsetForPageIndex(pageIndex);
        var readPerms = (byte)(vma.Perms & ~Protection.Write);

        var existingPrivate = privateObject?.GetPage(pageIndex) ?? IntPtr.Zero;
        if (!isWrite)
        {
            if (existingPrivate != IntPtr.Zero && privateObject?.PeekVmPage(pageIndex) is { } privatePage)
            {
                var binding = MappedPageBinding.FromAnonVmaPage(privateObject, pageIndex, privatePage);
                var result = EnsureExternalMapping(pageStart, binding, readPerms, engine);
                if (result == FaultResult.Handled && (vma.Perms & Protection.Exec) != 0)
                    TbCoh.ApplyWx(MemoryContext, binding.Ptr);
                return result;
            }

            return ResolveAndMapSharedBackingPage(vma, pageStart, pageIndex, vmaRelativeOffset, absoluteFileOffset,
                false, readPerms, engine);
        }

        if (existingPrivate != IntPtr.Zero)
            return EnsureWritableExistingPrivatePage(privateObject!, pageStart, pageIndex, existingPrivate,
                (byte)vma.Perms, engine);

        var sharedSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
            false, engine, out var sourcePage);
        if (sharedSourceResult != FaultResult.Handled)
            return sharedSourceResult;

        if (!TryAllocatePrivateCopyFromSource(engine, pageStart, sourcePage, "CowFirstPrivate",
                out var privatePageHandle))
            return FaultResult.Oom;

        Interlocked.Increment(ref _cowAllocFirstCount);
        if (privateObject == null)
        {
            privateObject = new AnonVma(MemoryContext);
            vma.VmAnonVma = privateObject;
            RegisterVmAreaAnonAttachment(vma);
        }

        return InstallPrivatePageAndMap(privateObject, pageStart, pageIndex, ref privatePageHandle, (byte)vma.Perms,
            engine,
            true);
    }

    public FaultResult HandleFaultDetailed(uint addr, bool isWrite, Engine engine)
    {
        var vma = FindVmArea(addr);
        if (vma == null)
        {
            Logger.LogWarning("No VmArea found for address 0x{Addr:x}", addr);
            return FaultResult.Segv;
        }

        if (vma.IsFileBacked)
            SyncFileBackedVmaMapping(vma);

        var pageStart = addr & LinuxConstants.PageMask;
        var pageIndex = GetVmaPageIndex(vma, pageStart);


        if (IsPrivateVma(vma))
            return ResolvePrivateFault(vma, pageStart, pageIndex, isWrite, engine);

        return ResolveSharedMappingFault(vma, addr, pageStart, pageIndex, isWrite, engine);
    }

    public bool HandleFault(uint addr, bool isWrite, Engine engine)
    {
        return HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
    }

    internal void MigrateOverlayMappings(OverlayInode overlayInode, Inode newBackingInode,
        IReadOnlyList<Engine> engines)
    {
        var invalidatedRanges = new List<NativeRange>();
        var replacementInode = newBackingInode as MappingBackedInode;
        if (replacementInode == null)
            return;

        foreach (var vma in _vmas)
        {
            if (!ReferenceEquals(vma.File?.OpenedInode, overlayInode))
                continue;

            var replacementMapping = replacementInode.AcquireMappingRef();
            if (ReferenceEquals(vma.VmMapping, replacementMapping))
            {
                replacementMapping.Release();
                continue;
            }

            vma.VmMapping?.RemoveRmapAttachments(vma);
            vma.VmMapping?.Release();
            vma.VmMapping = replacementMapping;
            RegisterVmAreaMappingAttachment(vma);
            invalidatedRanges.Add(new NativeRange(vma.Start, vma.Length));
        }

        if (invalidatedRanges.Count == 0)
            return;

        MergeRangesInPlace(invalidatedRanges);
        var sequence = BumpMapSequence();
        foreach (var range in invalidatedRanges)
            RecordCodeCacheResetRange(sequence, range.Start, range.Length);

        if (engines.Count == 0)
            return;

        var primary = engines[0];
        foreach (var range in invalidatedRanges)
            TearDownNativeMappings(primary, range.Start, range.Length, false, true, true);
        primary.AddressSpaceMapSequenceSeen = sequence;
    }

    internal bool PrefaultRange(uint addr, uint len, Engine engine, bool writeIntent)
    {
        if (len == 0) return true;
        var (start, endExclusive) = ComputePageAlignedRange(addr, len);
        for (var page = start; page < endExclusive; page += LinuxConstants.PageSize)
            if (HandleFaultDetailed((uint)page, writeIntent, engine) != FaultResult.Handled)
                return false;

        return true;
    }

    public void ShareAnonymousSharedPagesTo(VMAManager dest, Engine destEngine)
    {
        // No-op: shared anonymous memory is represented by shared address_space and materialized lazily.
    }

    private static bool IsSharedFileVmArea(VmArea vma)
    {
        return (vma.Flags & MapFlags.Shared) != 0 && vma.File?.OpenedInode != null;
    }

    private static bool TryGetSharedFileVmAreaState(VmArea vma, out LinuxFile file, out MappingBackedInode inode,
        out AddressSpace mapping)
    {
        file = null!;
        inode = null!;
        mapping = null!;
        if (!IsSharedFileVmArea(vma))
            return false;

        file = vma.File!;
        var mappedInode = file.OpenedInode as MappingBackedInode;
        if (mappedInode == null)
            return false;
        inode = mappedInode;

        return TryGetVmMapping(vma, out mapping);
    }

    private IEnumerable<VmArea> EnumerateSharedFileVmAreas(Inode? inode = null)
    {
        var snapshot = _vmas.ToArray();
        foreach (var vma in snapshot)
        {
            if (!TryGetSharedFileVmAreaState(vma, out _, out var openedInode, out _))
                continue;
            if (inode != null && !ReferenceEquals(openedInode, inode))
                continue;
            yield return vma;
        }
    }

    private static bool TryGetVmAreaOverlap(VmArea vma, uint rangeStart, uint rangeEnd, out uint overlapStart,
        out uint overlapEnd)
    {
        overlapStart = Math.Max(vma.Start, rangeStart);
        overlapEnd = Math.Min(vma.End, rangeEnd);
        return overlapStart < overlapEnd;
    }

    private static void CaptureDirtySharedFilePages(VmArea vma, Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (!TryGetSharedFileVmAreaState(vma, out _, out var inode, out var mapping))
            return;
        if (!TryGetVmAreaOverlap(vma, rangeStart, rangeEnd, out var captureStart, out var captureEnd))
            return;

        var startPage = (ulong)(captureStart & LinuxConstants.PageMask);
        var endPageExclusive = ((ulong)captureEnd + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;

        for (var page = startPage; page < endPageExclusive; page += LinuxConstants.PageSize)
        {
            var pageAddr = (uint)page;
            if (!engine.IsDirty(pageAddr)) continue;
            var pageIndex = vma.GetPageIndex(pageAddr);
            mapping.MarkDirty(pageIndex);
            inode.SetPageDirty(pageIndex);
        }
    }

    public static void SyncVmArea(VmArea vma, Engine engine)
    {
        SyncVmArea(vma, engine, vma.Start, vma.End);
    }

    public static void SyncVmArea(VmArea vma, IReadOnlyList<Engine> engines)
    {
        SyncVmArea(vma, engines, vma.Start, vma.End);
    }

    public void SyncMappedFile(LinuxFile file, IReadOnlyList<Engine> engines)
    {
        if (file.OpenedInode == null) return;
        var inode = file.OpenedInode;
        if (engines.Count == 0) return;
        foreach (var vma in EnumerateSharedFileVmAreas(inode))
            SyncVmArea(vma, engines);
    }

    public void SyncAllMappedSharedFiles(Engine engine)
    {
        SyncAllMappedSharedFiles([engine]);
    }

    public void SyncAllMappedSharedFiles(IReadOnlyList<Engine> engines)
    {
        if (engines.Count == 0) return;
        foreach (var vma in EnumerateSharedFileVmAreas())
            SyncVmArea(vma, engines);
    }

    public void CaptureDirtySharedPages(Engine engine)
    {
        CaptureDirtySharedPages(engine, 0, uint.MaxValue);
    }

    public void CaptureDirtySharedPages(Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (rangeStart >= rangeEnd) return;

        VisitVmAreasInRange(rangeStart, rangeEnd,
            vma => CaptureDirtySharedFilePages(vma, engine, rangeStart, rangeEnd));
    }

    public static void SyncVmArea(VmArea vma, Engine engine, uint rangeStart, uint rangeEnd)
    {
        SyncVmArea(vma, [engine], rangeStart, rangeEnd);
    }

    public static void SyncVmArea(VmArea vma, IReadOnlyList<Engine> engines, uint rangeStart, uint rangeEnd)
    {
        if (!TryGetSharedFileVmAreaState(vma, out _, out var inode, out var mapping))
            return;
        if (engines.Count == 0) return;
        if (!TryGetVmAreaOverlap(vma, rangeStart, rangeEnd, out var syncStart, out var syncEnd))
            return;

        var startPage = syncStart & LinuxConstants.PageMask;
        var endPage = (syncEnd + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (endPage < startPage) return;
        var startPageIndex = vma.GetPageIndex(startPage);
        var endPageIndex = endPage <= startPage
            ? startPageIndex
            : vma.GetPageIndex(endPage) - 1;

        for (var page = startPage; page < endPage; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.GetPageIndex(page);

            var isDirty = false;
            foreach (var engine in engines)
            {
                if (!engine.IsDirty(page)) continue;
                isDirty = true;
                break;
            }

            if (isDirty)
            {
                mapping.MarkDirty(pageIndex);
                inode.SetPageDirty(pageIndex);
            }
        }

        _ = inode.SyncCachedPages(vma.File, mapping, new WritePagesRequest(startPageIndex, endPageIndex, true));
    }

    public void LogVmAreas()
    {
        Logger.LogInformation("Memory Map:");
        foreach (var vma in _vmas)
            Logger.LogInformation("0x{Start:x8}-0x{End:x8} {Perms} {Flags} {Name}", vma.Start, vma.End, vma.Perms,
                vma.Flags, vma.Name);
    }

    internal readonly record struct NativeRange(uint Start, uint Length);

    private readonly record struct CodeCacheResetEntry(long Sequence, NativeRange Range);
}
