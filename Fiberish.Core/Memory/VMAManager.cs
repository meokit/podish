using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading;

namespace Fiberish.Memory;

public enum FaultResult
{
    Handled = 0,
    Segv = 1,
    BusError = 2
}

public class VMAManager
{
    private static readonly ILogger Logger = Logging.CreateLogger<VMAManager>();
    private static long _cowAllocFirstCount;
    private static long _cowAllocReplaceCount;
    private const int MaxInvalidationEntries = 4096;
    private long _mapSequence;
    private int _sharedRefCount = 1;
    private readonly List<VMA> _vmas = [];
    private VMA? _lastFaultVma;
    private readonly Dictionary<Inode, int> _mappedInodeRefCounts = [];
    internal readonly record struct NativeRange(uint Start, uint Length);
    private readonly record struct InvalidationEntry(long Sequence, NativeRange Range);
    private readonly List<InvalidationEntry> _pendingInvalidations = [];
    public ExternalPageManager ExternalPages { get; } = new();
    public MemoryObjectManager MemoryObjects { get; }

    public VMAManager(MemoryObjectManager? memoryObjects = null)
    {
        MemoryObjects = memoryObjects ?? new MemoryObjectManager();
    }

    public static (long First, long Replace) GetCowAllocationCounters()
    {
        return (Interlocked.Read(ref _cowAllocFirstCount), Interlocked.Read(ref _cowAllocReplaceCount));
    }

    public int GetSharedRefCount()
    {
        return Volatile.Read(ref _sharedRefCount);
    }

    public long CurrentMapSequence => Interlocked.Read(ref _mapSequence);

    internal long BumpMapSequence()
    {
        return Interlocked.Increment(ref _mapSequence);
    }

    internal void RecordInvalidationRange(long sequence, uint addr, uint len)
    {
        if (len == 0) return;
        var (start, endExclusive) = ComputePageAlignedRange(addr, len);
        if (endExclusive <= start) return;
        var range = new NativeRange((uint)start, (uint)(endExclusive - start));
        _pendingInvalidations.Add(new InvalidationEntry(sequence, range));
        if (_pendingInvalidations.Count > MaxInvalidationEntries)
            CompactInvalidationRanges(sequence);
    }

    internal long CollectInvalidationRangesSince(long seenSequence, List<NativeRange> output)
    {
        output.Clear();
        var current = CurrentMapSequence;
        if (seenSequence >= current) return current;

        foreach (var entry in _pendingInvalidations)
        {
            if (entry.Sequence <= seenSequence) continue;
            output.Add(entry.Range);
        }

        if (output.Count > 1)
            MergeRangesInPlace(output);

        return current;
    }

    internal void PruneInvalidationRanges(long minSeenSequence)
    {
        if (_pendingInvalidations.Count == 0) return;
        var removeCount = 0;
        while (removeCount < _pendingInvalidations.Count &&
               _pendingInvalidations[removeCount].Sequence <= minSeenSequence)
            removeCount++;
        if (removeCount > 0)
            _pendingInvalidations.RemoveRange(0, removeCount);
    }

    private void CompactInvalidationRanges(long sequence)
    {
        if (_pendingInvalidations.Count == 0) return;
        var ranges = new List<NativeRange>(_pendingInvalidations.Count);
        foreach (var entry in _pendingInvalidations)
            ranges.Add(entry.Range);
        MergeRangesInPlace(ranges);
        _pendingInvalidations.Clear();
        foreach (var range in ranges)
            _pendingInvalidations.Add(new InvalidationEntry(sequence, range));
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
        return 0;
    }

    public IReadOnlyList<VMA> VMAs => _vmas;

    private static Inode? ResolveMappedInode(VMA vma)
    {
        return vma.File?.OpenedInode;
    }

    private static MemoryObject? ShareCowObjectForSplit(VMA vma)
    {
        var cow = vma.CowObject;
        cow?.AddRef();
        return cow;
    }

    private void TrackMappedInodeOnVmaAdded(VMA vma)
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

    private void TrackMappedInodeOnVmaRemoved(VMA vma)
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

    private void InsertVmaSorted(VMA vma)
    {
        int left = 0;
        int right = _vmas.Count - 1;
        int insertIndex = _vmas.Count;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
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

    public VMA? FindVMA(uint addr)
    {
        var cached = _lastFaultVma;
        if (cached != null && addr >= cached.Start && addr < cached.End)
            return cached;

        int left = 0;
        int right = _vmas.Count - 1;
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
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

    public List<VMA> FindVMAsInRange(uint start, uint end)
    {
        var result = new List<VMA>();
        if (start >= end) return result;

        int left = 0;
        int right = _vmas.Count - 1;
        int firstMatch = _vmas.Count;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
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

        for (int i = firstMatch; i < _vmas.Count && _vmas[i].Start < end; i++)
            result.Add(_vmas[i]);

        return result;
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

        var endExclusive = (rawEndExclusive + LinuxConstants.PageOffsetMask) & (ulong)LinuxConstants.PageMask;
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
        {
            if (ExternalPages.TryGet((uint)page, out var ptr))
            {
                throw new InvalidOperationException(
                    $"{source}: stale external page mapping remains at 0x{page:x8}, ptr=0x{ptr.ToInt64():x}");
            }
        }
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
        bool invalidateCodeRange, bool releaseExternalPages)
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
            engine.InvalidateRange(addr, len);
        engine.MemUnmap(addr, len);
        ExternalPages.ReleaseRange(addr, len);
        AssertExternalPagesReleasedForRange(addr, len, "TearDownNativeMappings");
    }

    private List<NativeRange> ApplyFileTruncateMetadata(Inode inode, long newSize)
    {
        if (newSize < 0) newSize = 0;

        var ranges = new List<NativeRange>();
        var touchedObjects = new HashSet<MemoryObject>();
        var touchedCowObjects = new HashSet<MemoryObject>();
        foreach (var vma in _vmas)
        {
            if (!ReferenceEquals(vma.File?.OpenedInode, inode)) continue;

            var validBytes = newSize > vma.Offset ? newSize - vma.Offset : 0;
            if (validBytes > vma.Length) validBytes = vma.Length;
            vma.FileBackingLength = validBytes;
            touchedObjects.Add(vma.MemoryObject);
            if (vma.CowObject != null)
                touchedCowObjects.Add(vma.CowObject);

            if (validBytes >= vma.Length) continue;

            var tearDownFrom = validBytes <= 0
                ? vma.Start
                : vma.Start + (uint)(((validBytes + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize) *
                                     LinuxConstants.PageSize);
            if (tearDownFrom < vma.End)
                ranges.Add(new NativeRange(tearDownFrom, vma.End - tearDownFrom));
        }

        foreach (var memoryObject in touchedObjects)
            memoryObject.TruncateToSize(newSize);
        foreach (var cowObject in touchedCowObjects)
            cowObject.TruncateToSize(newSize);

        MergeRangesInPlace(ranges);
        return ranges;
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
            RecordInvalidationRange(sequence, range.Start, range.Length);

        if (engines.Count == 0)
        {
            foreach (var range in ranges)
                AssertExternalPagesReleasedForRange(range.Start, range.Length, "OnFileTruncate.no-engines");
            return;
        }

        var primary = engines[0];
        foreach (var engine in engines)
        {
            if (engine.CurrentMmuIdentity != primary.CurrentMmuIdentity)
            {
                throw new InvalidOperationException(
                    "OnFileTruncate requires all engines in the same address space to share one MMU core.");
            }
        }

        foreach (var range in ranges)
            TearDownNativeMappings(
                primary,
                range.Start,
                range.Length,
                captureDirtySharedPages: false,
                invalidateCodeRange: true,
                releaseExternalPages: true);

        primary.AddressSpaceMapSequenceSeen = sequence;

        foreach (var range in ranges)
            AssertExternalPagesReleasedForRange(range.Start, range.Length, "OnFileTruncate");
    }

    public uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, LinuxFile? file, long offset, long filesz,
        string name, Engine engine)
    {
        // Align to 4k
        if ((addr & LinuxConstants.PageOffsetMask) != 0)
            throw new ArgumentException("Address not aligned");

        // Round up len to 4k
        len = (len + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (len == 0)
            throw new ArgumentException("Mapping length must be non-zero", nameof(len));

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
            throw new InvalidOperationException("Overlap detected");

        var isShared = (flags & MapFlags.Shared) != 0;
        MemoryObject memObj;
        MemoryObject? cowObj = null;
        uint viewPageOff = 0;
        VmaFileMapping? fileMapping = null;

        if (file == null)
        {
            // Anonymous mapping
            memObj = MemoryObjects.CreateAnonymous(isShared);
        }
        else if (isShared)
        {
            // MAP_SHARED file: share the inode's global page cache
            memObj = MemoryObjects.GetOrCreateInodePageCache(file.OpenedInode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
            fileMapping = new VmaFileMapping(file);
        }
        else
        {
            // MAP_PRIVATE file (including ELF_LOAD): shared inode cache as read-only source + private COW object
            memObj = MemoryObjects.GetOrCreateInodePageCache(file.OpenedInode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
            cowObj = MemoryObjects.CreateAnonymous(shared: false);
            fileMapping = new VmaFileMapping(file);
        }


        var vma = new VMA
        {
            Start = addr,
            End = end,
            Perms = perms,
            Flags = flags,
            FileMapping = fileMapping,
            Offset = offset,
            FileBackingLength = filesz,
            Name = name,
            MemoryObject = memObj,
            CowObject = cowObj,
            ViewPageOffset = viewPageOff
        };

        InsertVmaSorted(vma);

        return addr;
    }

    public VMAManager Clone()
    {
        var newMM = new VMAManager(MemoryObjects);
        foreach (var vma in _vmas)
        {
            var cloned = vma.Clone();
            newMM._vmas.Add(cloned);
            newMM.TrackMappedInodeOnVmaAdded(cloned);
        }
        return newMM;
    }

    /// <summary>
    /// Add a pre-constructed VMA directly. Used by SysV SHM subsystem.
    /// </summary>
    internal void AddVmaInternal(VMA vma)
    {
        InsertVmaSorted(vma);
    }

    public void Munmap(uint addr, uint length, Engine engine)
    {
        if (length == 0) return;
        uint end;
        try
        {
            end = checked(addr + length);
        }
        catch (OverflowException)
        {
            return;
        }
        
        for (var i = 0; i < _vmas.Count; i++)
        {
            var vma = _vmas[i];

            // No intersection
            if (end <= vma.Start || addr >= vma.End)
                continue;

            // Full removal
            if (addr <= vma.Start && end >= vma.End)
            {
                SyncVMA(vma, engine, vma.Start, vma.End);
                if (ReferenceEquals(_lastFaultVma, vma)) _lastFaultVma = null;
                vma.MemoryObject.Release();
                vma.CowObject?.Release();
                vma.FileMapping?.Release();
                TrackMappedInodeOnVmaRemoved(vma);
                _vmas.RemoveAt(i--);
                continue;
            }

            // Split (Middle removal)
            if (addr > vma.Start && end < vma.End)
            {
                SyncVMA(vma, engine, addr, end);
                var tailStart = end;
                var tailEnd = vma.End;
                long tailOffset = 0;
                long tailFileSz = 0;

                if (vma.File != null)
                {
                    long diff = tailStart - vma.Start;
                    tailOffset = vma.Offset + diff;
                    if (vma.FileBackingLength > diff)
                        tailFileSz = vma.FileBackingLength - diff;
                }

                var tailVMA = new VMA
                {
                    Start = tailStart,
                    End = tailEnd,
                    Perms = vma.Perms,
                    Flags = vma.Flags,
                    FileMapping = vma.FileMapping?.AddRef(),
                    Offset = tailOffset,
                    FileBackingLength = tailFileSz,
                    Name = vma.Name,
                    MemoryObject = vma.MemoryObject,
                    CowObject = ShareCowObjectForSplit(vma),
                    ViewPageOffset = vma.ViewPageOffset + ((tailStart - vma.Start) / LinuxConstants.PageSize)
                };
                vma.MemoryObject.AddRef();

                _vmas.Insert(i + 1, tailVMA);
                TrackMappedInodeOnVmaAdded(tailVMA);

                // Truncate current (head)
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileBackingLength > newLen)
                        vma.FileBackingLength = newLen;
                }

                continue;
            }

            // Head removal
            if (addr <= vma.Start && end < vma.End)
            {
                SyncVMA(vma, engine, vma.Start, end);
                var diff = end - vma.Start;
                vma.Start = end;
                vma.ViewPageOffset += diff / LinuxConstants.PageSize;
                if (vma.File != null)
                {
                    vma.Offset += diff;
                    if (vma.FileBackingLength > diff)
                        vma.FileBackingLength -= diff;
                    else
                        vma.FileBackingLength = 0;
                }

                continue;
            }

            // Tail removal
            if (addr > vma.Start && end >= vma.End)
            {
                SyncVMA(vma, engine, addr, vma.End);
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileBackingLength > newLen)
                        vma.FileBackingLength = newLen;
                }
            }
        }

        TearDownNativeMappings(
            engine,
            addr,
            length,
            captureDirtySharedPages: false,
            invalidateCodeRange: true,
            releaseExternalPages: true);
    }

    public int Mprotect(uint addr, uint len, Protection prot, Engine engine)
    {
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

        var vmas = FindVMAsInRange(addr, end);
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

        for (var i = 0; i < _vmas.Count; i++)
        {
            var vma = _vmas[i];
            var overlapStart = Math.Max(vma.Start, addr);
            var overlapEnd = Math.Min(vma.End, end);
            if (overlapStart >= overlapEnd) continue;

            var oldStart = vma.Start;
            var oldEnd = vma.End;
            var oldPerms = vma.Perms;
            var oldOffset = vma.Offset;
            var oldFileSz = vma.FileBackingLength;
            var oldViewPageOffset = vma.ViewPageOffset;

            // Fully covered: just flip perms.
            if (overlapStart == oldStart && overlapEnd == oldEnd)
            {
                vma.Perms = prot;
                continue;
            }

            // Prepare middle (the protected slice).
            var midDiff = (long)overlapStart - oldStart;
            var originalFileMapping = vma.FileMapping;
            var mid = new VMA
            {
                Start = overlapStart,
                End = overlapEnd,
                Perms = prot,
                Flags = vma.Flags,
                FileMapping = null,
                Offset = vma.File != null ? oldOffset + midDiff : 0,
                FileBackingLength = 0,
                Name = vma.Name,
                MemoryObject = vma.MemoryObject,
                CowObject = ShareCowObjectForSplit(vma),
                ViewPageOffset = oldViewPageOffset + ((uint)midDiff / LinuxConstants.PageSize)
            };
            vma.MemoryObject.AddRef();
            if (vma.File != null)
            {
                var remain = oldFileSz - midDiff;
                if (remain < 0) remain = 0;
                var midLen = (long)(mid.End - mid.Start);
                mid.FileBackingLength = Math.Min(remain, midLen);
            }

            // Left tail stays in the existing VMA.
            var hasLeft = overlapStart > oldStart;
            var hasRight = overlapEnd < oldEnd;
            mid.FileMapping = hasLeft ? originalFileMapping?.AddRef() : originalFileMapping;

            if (hasLeft)
            {
                vma.Start = oldStart;
                vma.End = overlapStart;
                vma.Perms = oldPerms;
                if (vma.File != null)
                {
                    var leftLen = (long)(vma.End - vma.Start);
                    vma.FileBackingLength = Math.Min(oldFileSz, leftLen);
                }
            }

            // Right tail (if any) keeps old perms.
            VMA? right = null;
            if (hasRight)
            {
                var rightDiff = (long)overlapEnd - oldStart;
                right = new VMA
                {
                    Start = overlapEnd,
                    End = oldEnd,
                    Perms = oldPerms,
                    Flags = vma.Flags,
                    FileMapping = originalFileMapping?.AddRef(),
                    Offset = vma.File != null ? oldOffset + rightDiff : 0,
                    FileBackingLength = 0,
                    Name = vma.Name,
                    MemoryObject = vma.MemoryObject,
                    CowObject = ShareCowObjectForSplit(vma),
                    ViewPageOffset = oldViewPageOffset + ((uint)rightDiff / LinuxConstants.PageSize)
                };
                vma.MemoryObject.AddRef();
                if (vma.File != null)
                {
                    var remain = oldFileSz - rightDiff;
                    if (remain < 0) remain = 0;
                    var rightLen = (long)(right.End - right.Start);
                    right.FileBackingLength = Math.Min(remain, rightLen);
                }
            }

            if (!hasLeft)
            {
                // Reuse current slot for middle when there is no left tail.
                // Move CowObject ownership to reused VMA slot; otherwise mid.CowObject is leaked.
                var oldCow = vma.CowObject;
                vma.Start = mid.Start;
                vma.End = mid.End;
                vma.Perms = mid.Perms;
                vma.FileMapping = mid.FileMapping;
                vma.Offset = mid.Offset;
                vma.FileBackingLength = mid.FileBackingLength;
                vma.Name = mid.Name;
                vma.ViewPageOffset = mid.ViewPageOffset;
                vma.CowObject = mid.CowObject;
                mid.CowObject = null;
                mid.FileMapping = null;
                // Drop the temporary extra ref held by mid.
                mid.MemoryObject.Release();
                oldCow?.Release();

                if (right != null)
                {
                    _vmas.Insert(i + 1, right);
                    TrackMappedInodeOnVmaAdded(right);
                    i++;
                }
            }
            else
            {
                // Keep left in current slot; insert middle and optional right.
                _vmas.Insert(i + 1, mid);
                TrackMappedInodeOnVmaAdded(mid);
                if (right != null)
                {
                    _vmas.Insert(i + 2, right);
                    TrackMappedInodeOnVmaAdded(right);
                }
                i += right != null ? 2 : 1;
            }
        }

        // Capture shared dirty bits before dropping native mappings for this engine.
        // MemUnmap below forces refault with updated VMA perms without clobbering dirty state.
        TearDownNativeMappings(
            engine,
            addr,
            len,
            captureDirtySharedPages: true,
            invalidateCodeRange: true,
            releaseExternalPages: true);

        return 0;
    }

    public void Clear(Engine engine)
    {
        foreach (var vma in _vmas)
        {
            SyncVMA(vma, engine);
            // Clear native mappings through unified teardown path.
            TearDownNativeMappings(
                engine,
                vma.Start,
                vma.End - vma.Start,
                captureDirtySharedPages: false,
                invalidateCodeRange: true,
                releaseExternalPages: true);
            vma.MemoryObject.Release();
            vma.CowObject?.Release();
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
            {
                // Collision! We can't put it here.
                // The next possible spot is right after this colliding VMA.
                baseAddr = (vma.End + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask; // Ensure page alignment
            }
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

    public FaultResult HandleFaultDetailed(uint addr, bool isWrite, Engine engine)
    {
        var vma = FindVMA(addr);
        if (vma == null)
        {
            Logger.LogWarning("No VMA found for address 0x{Addr:x}", addr);
            return FaultResult.Segv;
        }

        var pageStart = addr & LinuxConstants.PageMask;
        var pageIndex = vma.ViewPageOffset + ((pageStart - vma.Start) / LinuxConstants.PageSize);


        // ── COW path: MAP_PRIVATE file mmap ──────────────────────────────────────
        if (vma.CowObject != null)
        {
            if (isWrite)
            {
                var existingCow = vma.CowObject.GetPage(pageIndex);
                if (existingCow != IntPtr.Zero)
                {
                    // Already in CowObject.
                    // 1. Strip Write permission immediately and Yield to flush uTLB across CPUs
                    engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)(vma.Perms & ~Protection.Write));
                    engine.Yield();

                    var hasCurrentMapping = ExternalPages.TryGet(pageStart, out var mappedPtr);
                    var mapsExistingCow = hasCurrentMapping && mappedPtr == existingCow;

                    // 2. Check reference count to see if we have exclusive ownership.
                    // An exclusively mapped COW page will have a global ref count of 2:
                    // 1 reference owned by the vma.CowObject
                    // 1 reference owned by the ExternalPages tracking (current VMA)
                    if (mapsExistingCow && ExternalPageManager.GetRefCount(existingCow) == 2)
                    {
                        // We are the exclusive owner. No need to copy, just upgrade to Writable.
                        engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);
                        return FaultResult.Handled;
                    }
                    else
                    {
                        if (!hasCurrentMapping)
                        {
                            if (!ExternalPages.AddMapping(pageStart, existingCow, out var addedRef))
                                return FaultResult.Segv;
                            if (!engine.MapExternalPage(pageStart, existingCow, (byte)(vma.Perms & ~Protection.Write)))
                            {
                                if (addedRef) ExternalPages.Release(pageStart);
                                return FaultResult.Segv;
                            }
                        }

                        // Page is shared (e.g. after fork). Must Copy-On-Write.
                        if (!ExternalPageManager.TryAllocateExternalPageStrict(out var newPage, AllocationClass.Cow,
                                AllocationSource.CowReplacePrivate))
                            return FaultResult.Segv;
                        Interlocked.Increment(ref _cowAllocReplaceCount);
                        var owner = engine.Owner as FiberTask;
                        Logger.LogTrace(
                            "[COW] Allocate replacement page pid={Pid} vma={Vma} pageIndex={PageIndex} addr=0x{PageStart:x}",
                            owner?.PID ?? 0, vma.Name, pageIndex, pageStart);

                        unsafe
                        {
                            Buffer.MemoryCopy((void*)existingCow, (void*)newPage,
                                LinuxConstants.PageSize, LinuxConstants.PageSize);
                        }

                        // Update CowObject and adjust reference counts.
                        // AllocateExternalPage already gives newPage RefCount=1 (owned by CowObject)
                        vma.CowObject.SetPage(pageIndex, newPage);

                        ExternalPageManager.ReleasePtr(existingCow); // Drop CowObject's old ref to existingCow

                        // Remap in Engine
                        ExternalPages.Release(pageStart); // drops old mapping: existingCow loses 1 ref
                        if (!ExternalPages.AddMapping(pageStart, newPage, out _))
                            return FaultResult.Segv; // newPage gains 1 ref -> Total 2
                        if (!engine.MapExternalPage(pageStart, newPage, (byte)vma.Perms)) return FaultResult.Segv;
                        return FaultResult.Handled;
                    }
                }
                else
                {
                    // Not in CowObject yet. We need to copy from the inode cache.
                    // 1. Relative coordinate (Relative to VMA Start)
                    long vmaRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
                    // 2. Absolute coordinate (Absolute within the File)
                    var absoluteFileOffset = vma.Offset + vmaRelativeOffset;
                    if (vma.File != null && vmaRelativeOffset >= vma.FileBackingLength)
                        return FaultResult.BusError;

                    IntPtr srcPage;
                    if (!TryResolveMappedFilePage(vma, pageIndex, absoluteFileOffset, out srcPage))
                    {
                        srcPage = vma.MemoryObject.GetOrCreatePage(pageIndex, ptr =>
                        {
                            if (vma.File == null)
                            {
                                unsafe
                                {
                                    new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                                }

                                return true;
                            }

                            var readLen = LinuxConstants.PageSize;
                            if (vma.FileBackingLength > 0)
                            {
                                // Relative length - relative offset = valid file bytes remaining to read in this page
                                var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;

                                if (remainingBackingBytes <= 0) readLen = 0;
                                else if (remainingBackingBytes < LinuxConstants.PageSize)
                                    readLen = (int)remainingBackingBytes;
                            }

                            unsafe
                            {
                                Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                                var req = new PageIoRequest(pageIndex, absoluteFileOffset, Math.Max(0, readLen));
                                var rc = vma.File.OpenedInode!.ReadPage(vma.File, req, buf);
                                if (rc < 0) return false;
                            }

                            return true;
                        }, out _);
                    }

                    if (srcPage == IntPtr.Zero) return FaultResult.Segv;

                    // Allocate private copy from inode cache
                    if (!ExternalPageManager.TryAllocateExternalPageStrict(out existingCow, AllocationClass.Cow,
                            AllocationSource.CowFirstPrivate))
                        return FaultResult.Segv;
                    Interlocked.Increment(ref _cowAllocFirstCount);
                    var owner2 = engine.Owner as FiberTask;
                    Logger.LogTrace(
                        "[COW] Allocate first private page pid={Pid} vma={Vma} pageIndex={PageIndex} addr=0x{PageStart:x}",
                        owner2?.PID ?? 0, vma.Name, pageIndex, pageStart);

                    unsafe
                    {
                        Buffer.MemoryCopy((void*)srcPage, (void*)existingCow,
                            LinuxConstants.PageSize, LinuxConstants.PageSize);
                    }

                    // AllocateExternalPage() gives existingCow RefCount=1. 
                    // This first reference is implicitly owned by CowObject.
                    vma.CowObject.SetPage(pageIndex, existingCow);

                    // Replace old read-only mapping with writable COW page
                    ExternalPages.Release(pageStart); // drop old inode cache ref from VMA

                    // Add mapping gives existingCow a +1 ref (Total 2)
                    if (!ExternalPages.AddMapping(pageStart, existingCow, out _)) return FaultResult.Segv;
                    if (!engine.MapExternalPage(pageStart, existingCow, (byte)vma.Perms)) return FaultResult.Segv; // full perms
                    return FaultResult.Handled;
                }
            }
            else
            {
                // Read access: prefer COW page if it exists, otherwise inode cache (read-only map)
                var cowPage = vma.CowObject.GetPage(pageIndex);
                if (cowPage != IntPtr.Zero)
                {
                    if (!ExternalPages.AddMapping(pageStart, cowPage, out var addedRef)) return FaultResult.Segv;
                    var readPerms = (byte)(vma.Perms & ~Protection.Write);
                    if (!engine.MapExternalPage(pageStart, cowPage, readPerms))
                    {
                        if (addedRef) ExternalPages.Release(pageStart);
                        return FaultResult.Segv;
                    }

                    return FaultResult.Handled;
                }
                // Fall through to normal read path, but map READ-ONLY
                // (so a subsequent write will fault back here for COW)
            }
        }

        // ── Existing logic (unchanged) ───────────────────────────────────────────
        if (isWrite && (vma.Perms & Protection.Write) == 0 && vma.CowObject == null)
        {
            Logger.LogTrace("Write fault on read-only VMA: {VmaName} at 0x{Addr:x}", vma.Name, addr);
            return FaultResult.Segv;
        }

        var mapPerms = (vma.CowObject != null)
            ? (vma.Perms & ~Protection.Write) // read-only: write fault triggers COW
            : vma.Perms;

        var tempPerms = mapPerms | Protection.Write;

        // For file-backed mappings, try filesystem-provided direct file mapping first.
        // This makes mmap-shared dirty pages survive process termination through host kernel page cache.
        long normalRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
        var normalAbsoluteFileOffset = vma.Offset + normalRelativeOffset;
        if (vma.File != null && normalRelativeOffset >= vma.FileBackingLength)
            return FaultResult.BusError;
        var strictQuota = vma.File == null;
        var allocationClass = strictQuota ? AllocationClass.Anonymous : AllocationClass.PageCache;

        IntPtr hostPtr;
        if (!TryResolveMappedFilePage(vma, pageIndex, normalAbsoluteFileOffset, out hostPtr))
        {
            hostPtr = vma.MemoryObject.GetOrCreatePage(pageIndex, ptr =>
            {
                if (vma.File == null)
                {
                    unsafe
                    {
                        new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                    }

                    return true;
                }

                var readLen = LinuxConstants.PageSize;
                if (vma.FileBackingLength > 0)
                {
                    // Relative length - relative offset = valid file bytes remaining to read in this page
                    var remainingBackingBytes = vma.FileBackingLength - normalRelativeOffset;

                    if (remainingBackingBytes <= 0) readLen = 0;
                    else if (remainingBackingBytes < LinuxConstants.PageSize) readLen = (int)remainingBackingBytes;
                }

                unsafe
                {
                    Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                    var req = new PageIoRequest(pageIndex, normalAbsoluteFileOffset, Math.Max(0, readLen));
                    var rc = vma.File.OpenedInode!.ReadPage(vma.File, req, buf);
                    if (rc < 0) return false;
                }

                return true;
            }, out _, strictQuota, allocationClass, strictQuota ? AllocationSource.AnonFault : AllocationSource.Unknown);
        }
        if (hostPtr == IntPtr.Zero)
        {
            var allocatedBytes = ExternalPageManager.GetAllocatedBytes();
            var quotaBytes = ExternalPageManager.MemoryQuotaBytes;
            var (strictSuccess, strictReclaimSuccess, strictFail, legacyOverQuota) =
                ExternalPageManager.GetAllocationStats();
            Logger.LogError(
                "HandleFault: object page allocation failed page=0x{PageStart:x} fault=0x{Addr:x} write={IsWrite} " +
                "vma={VmaName} perms={Perms} pageIndex={PageIndex} strictQuota={StrictQuota} class={AllocationClass} " +
                "quotaBytes={QuotaBytes} allocatedBytes={AllocatedBytes} " +
                "strictSuccess={StrictSuccess} strictReclaimSuccess={StrictReclaimSuccess} strictFail={StrictFail} " +
                "legacyOverQuota={LegacyOverQuota}",
                pageStart, addr, isWrite, vma.Name, vma.Perms, pageIndex, strictQuota, allocationClass,
                quotaBytes, allocatedBytes,
                strictSuccess, strictReclaimSuccess, strictFail, legacyOverQuota);
            return FaultResult.Segv;
        }

        if (!ExternalPages.AddMapping(pageStart, hostPtr, out var added))
        {
            Logger.LogError("HandleFault: page mapping mismatch for 0x{PageStart:x}", pageStart);
            return FaultResult.Segv;
        }

        if (!engine.MapExternalPage(pageStart, hostPtr, (byte)tempPerms))
        {
            if (added) ExternalPages.Release(pageStart);
            Logger.LogError("HandleFault: MapExternalPage failed for 0x{PageStart:x}", pageStart);
            return FaultResult.Segv;
        }

        // Set final permissions
        if (tempPerms != mapPerms) engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)mapPerms);

        return FaultResult.Handled;
    }

    public bool HandleFault(uint addr, bool isWrite, Engine engine)
    {
        return HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
    }

    private static bool TryResolveMappedFilePage(VMA vma, uint pageIndex, long absoluteFileOffset, out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        var inode = vma.File?.OpenedInode;
        if (inode == null) return false;
        if (!inode.TryAcquireMappedPageHandle(vma.File, pageIndex, absoluteFileOffset, out var pageHandle))
            return false;
        if (pageHandle == null) return false;

        var mappedPtr = pageHandle.Pointer;
        if (mappedPtr == IntPtr.Zero)
        {
            pageHandle.Dispose();
            return false;
        }

        var existing = vma.MemoryObject.GetPage(pageIndex);
        if (existing != IntPtr.Zero)
        {
            pageHandle.Dispose();
            pagePtr = existing;
            return true;
        }

        ExternalPageManager.AddRefPtr(mappedPtr, pageHandle);
        var finalPtr = vma.MemoryObject.SetPageIfAbsent(pageIndex, mappedPtr, out var inserted);
        if (!inserted)
            ExternalPageManager.ReleasePtr(mappedPtr);

        pagePtr = finalPtr;
        return pagePtr != IntPtr.Zero;
    }

    public bool MapAnonymousPage(uint addr, Engine engine, Protection perms)
    {
        var pageStart = addr & LinuxConstants.PageMask;
        var vma = FindVMA(pageStart);
        if (vma == null)
            return false;

        var pageIndex = vma.ViewPageOffset + ((pageStart - vma.Start) / LinuxConstants.PageSize);
        var hostPtr = vma.MemoryObject.GetOrCreatePage(
            pageIndex,
            onFirstCreate: null,
            out _,
            strictQuota: true,
            AllocationClass.Anonymous,
            AllocationSource.AnonMapPreFault);
        if (hostPtr == IntPtr.Zero) return false;
        if (!ExternalPages.AddMapping(pageStart, hostPtr, out var added))
            return false;

        if (!engine.MapExternalPage(pageStart, hostPtr, (byte)perms))
        {
            if (added) ExternalPages.Release(pageStart);
            return false;
        }

        return true;
    }

    public void ShareAnonymousSharedPagesTo(VMAManager dest, Engine destEngine)
    {
        // No-op: shared memory is represented by shared MemoryObject and materialized lazily.
    }

    public static void SyncVMA(VMA vma, Engine engine)
    {
        SyncVMA(vma, engine, vma.Start, vma.End);
    }

    public static void SyncVMA(VMA vma, IReadOnlyList<Engine> engines)
    {
        SyncVMA(vma, engines, vma.Start, vma.End);
    }

    public void SyncMappedFile(LinuxFile file, Engine engine)
    {
        SyncMappedFile(file, [engine]);
    }

    public void SyncMappedFile(LinuxFile file, IReadOnlyList<Engine> engines)
    {
        if (file.OpenedInode == null) return;
        var inode = file.OpenedInode;
        if (engines.Count == 0) return;
        var snapshot = _vmas.ToArray();
        foreach (var vma in snapshot)
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            if (!ReferenceEquals(vma.File.OpenedInode, inode)) continue;
            SyncVMA(vma, engines);
        }
    }

    public void SyncAllMappedSharedFiles(Engine engine)
    {
        SyncAllMappedSharedFiles([engine]);
    }

    public void SyncAllMappedSharedFiles(IReadOnlyList<Engine> engines)
    {
        if (engines.Count == 0) return;
        var snapshot = _vmas.ToArray();
        foreach (var vma in snapshot)
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            SyncVMA(vma, engines);
        }
    }

    public void CaptureDirtySharedPages(Engine engine)
    {
        CaptureDirtySharedPages(engine, 0, uint.MaxValue);
    }

    public void CaptureDirtySharedPages(Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (rangeStart >= rangeEnd) return;

        var vmas = FindVMAsInRange(rangeStart, rangeEnd);
        foreach (var vma in vmas)
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            var inode = vma.File.OpenedInode;
            if (inode == null) continue;

            var captureStart = Math.Max(vma.Start, rangeStart);
            var captureEnd = Math.Min(vma.End, rangeEnd);
            if (captureStart >= captureEnd) continue;

            ulong startPage = (ulong)(captureStart & LinuxConstants.PageMask);
            ulong endPageExclusive = ((ulong)captureEnd + LinuxConstants.PageOffsetMask) &
                                     (ulong)LinuxConstants.PageMask;

            for (ulong page = startPage; page < endPageExclusive; page += (ulong)LinuxConstants.PageSize)
            {
                var pageAddr = (uint)page;
                var vmaRelativeOffset = pageAddr - vma.Start;
                if (!engine.IsDirty(pageAddr)) continue;
                var pageIndex = vma.ViewPageOffset + (vmaRelativeOffset / LinuxConstants.PageSize);
                vma.MemoryObject.MarkDirty(pageIndex);
                inode.SetPageDirty(pageIndex);
            }
        }
    }

    public static void SyncVMA(VMA vma, Engine engine, uint rangeStart, uint rangeEnd)
    {
        SyncVMA(vma, [engine], rangeStart, rangeEnd);
    }

    public static void SyncVMA(VMA vma, IReadOnlyList<Engine> engines, uint rangeStart, uint rangeEnd)
    {
        if (vma.File == null || (vma.Flags & MapFlags.Shared) == 0)
            return;
        if (engines.Count == 0) return;

        var syncStart = Math.Max(vma.Start, rangeStart);
        var syncEnd = Math.Min(vma.End, rangeEnd);
        if (syncStart >= syncEnd) return;
        var inode = vma.File.OpenedInode;
        if (inode == null) return;

        var startPage = syncStart & LinuxConstants.PageMask;
        var endPage = (syncEnd + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (endPage < startPage) return;
        var startPageIndex = vma.ViewPageOffset + ((startPage - vma.Start) / LinuxConstants.PageSize);
        var endPageIndex = endPage <= startPage
            ? startPageIndex
            : vma.ViewPageOffset + ((endPage - vma.Start) / LinuxConstants.PageSize) - 1;

        for (var page = startPage; page < endPage; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.ViewPageOffset + ((page - vma.Start) / LinuxConstants.PageSize);

            var isDirty = false;
            foreach (var engine in engines)
            {
                if (!engine.IsDirty(page)) continue;
                isDirty = true;
                break;
            }

            if (isDirty)
            {
                vma.MemoryObject.MarkDirty(pageIndex);
                inode.SetPageDirty(pageIndex);
            }

            if (!vma.MemoryObject.IsDirty(pageIndex)) continue;
            var pagePtr = vma.MemoryObject.GetPage(pageIndex);
            if (pagePtr == IntPtr.Zero) continue;

            // 1. Relative coordinate (Relative to VMA Start)
            long vmaRelativeOffset = page - vma.Start;
            // 2. Absolute coordinate (Absolute within the File)
            var absoluteFileOffset = vma.Offset + vmaRelativeOffset;

            var writeLen = LinuxConstants.PageSize;
            if (vma.FileBackingLength > 0)
            {
                var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;
                if (remainingBackingBytes <= 0)
                    writeLen = 0;
                else if (remainingBackingBytes < LinuxConstants.PageSize)
                    writeLen = (int)remainingBackingBytes;
            }

            if (writeLen <= 0)
            {
                continue;
            }

            unsafe
            {
                ReadOnlySpan<byte> pageData = new((void*)pagePtr, LinuxConstants.PageSize);
                GlobalPageCacheManager.BeginWritebackPages();
                try
                {
                    var rc = inode.WritePage(vma.File, new PageIoRequest(pageIndex, absoluteFileOffset, writeLen),
                        pageData, true);
                    if (rc == 0)
                        vma.MemoryObject.ClearDirty(pageIndex);
                }
                finally
                {
                    GlobalPageCacheManager.EndWritebackPages();
                }
            }
        }

        inode.WritePages(vma.File, new WritePagesRequest(startPageIndex, endPageIndex, true));
    }

    public void LogVMAs()
    {
        Logger.LogInformation("Memory Map:");
        foreach (var vma in _vmas)
            Logger.LogInformation("0x{Start:x8}-0x{End:x8} {Perms} {Flags} {Name}", vma.Start, vma.End, vma.Perms,
                vma.Flags, vma.Name);
    }
}
