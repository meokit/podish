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
    private readonly List<VMA> _vmas = [];
    private VMA? _lastFaultVma;
    private long _mapSequence;
    private int _sharedRefCount = 1;

    public VMAManager(MemoryObjectManager? memoryObjects = null)
    {
        MemoryObjects = memoryObjects ?? new MemoryObjectManager();
    }

    public ExternalPageManager ExternalPages { get; } = new();
    public MemoryObjectManager MemoryObjects { get; }

    public long CurrentMapSequence => Interlocked.Read(ref _mapSequence);

    public IReadOnlyList<VMA> VMAs => _vmas;

    public static (long First, long Replace) GetCowAllocationCounters()
    {
        return (Interlocked.Read(ref _cowAllocFirstCount), Interlocked.Read(ref _cowAllocReplaceCount));
    }

    public int GetSharedRefCount()
    {
        return Volatile.Read(ref _sharedRefCount);
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
        return 0;
    }

    private static Inode? ResolveMappedInode(VMA vma)
    {
        return vma.File?.OpenedInode;
    }

    private static MemoryObject? SharePrivateObjectForSplit(VMA vma)
    {
        var privateObject = vma.PrivateObject;
        privateObject?.AddRef();
        return privateObject;
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

    public VMA? FindVMA(uint addr)
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

    public List<VMA> FindVMAsInRange(uint start, uint end)
    {
        var result = new List<VMA>();
        if (start >= end) return result;

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
            if (ExternalPages.TryGet((uint)page, out var ptr))
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
            engine.ResetCodeCacheByRange(addr, len);
        engine.MemUnmap(addr, len);
        ExternalPages.ReleaseRange(addr, len);
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

    public void RebuildExternalMappingsFromNative(Engine engine, IEnumerable<VMA> vmas)
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
                    ExternalPages.AddMapping(mapping.GuestPage, mapping.HostPage, out _);
                }

                cursor += chunkLen;
            }
        }
    }

    private List<NativeRange> ApplyFileTruncateMetadata(Inode inode, long newSize)
    {
        if (newSize < 0) newSize = 0;

        var ranges = new List<NativeRange>();
        var touchedSharedObjects = new HashSet<MemoryObject>();
        var touchedPrivateObjects = new HashSet<MemoryObject>();
        foreach (var vma in _vmas)
        {
            if (!ReferenceEquals(vma.File?.OpenedInode, inode)) continue;

            var validBytes = newSize > vma.Offset ? newSize - vma.Offset : 0;
            if (validBytes > vma.Length) validBytes = vma.Length;
            vma.FileBackingLength = validBytes;
            touchedSharedObjects.Add(vma.SharedObject);
            if (vma.PrivateObject != null)
                touchedPrivateObjects.Add(vma.PrivateObject);

            if (validBytes >= vma.Length) continue;

            var tearDownFrom = validBytes <= 0
                ? vma.Start
                : vma.Start + (uint)((validBytes + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize *
                                     LinuxConstants.PageSize);
            if (tearDownFrom < vma.End)
                ranges.Add(new NativeRange(tearDownFrom, vma.End - tearDownFrom));
        }

        foreach (var sharedObject in touchedSharedObjects)
            sharedObject.TruncateToSize(newSize);
        foreach (var privateObject in touchedPrivateObjects)
            privateObject.TruncateToSize(newSize);

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
            RecordCodeCacheResetRange(sequence, range.Start, range.Length);

        if (engines.Count == 0)
        {
            foreach (var range in ranges)
                AssertExternalPagesReleasedForRange(range.Start, range.Length, "OnFileTruncate.no-engines");
            return;
        }

        var primary = engines[0];
        foreach (var engine in engines)
            if (engine.CurrentMmuIdentity != primary.CurrentMmuIdentity)
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
        var isPrivate = (flags & MapFlags.Private) != 0;
        MemoryObject sharedObj;
        MemoryObject? privateObj = null;
        uint viewPageOff = 0;
        VmaFileMapping? fileMapping = null;

        if (file == null)
        {
            sharedObj = isShared
                ? MemoryObjects.CreateSharedAnonymous()
                : MemoryObjects.CreateAnonymousSharedSource();
            if (isPrivate)
                privateObj = MemoryObjects.CreatePrivateOverlay();
        }
        else if (isShared)
        {
            // MAP_SHARED file: share the inode's global page cache
            sharedObj = MemoryObjects.GetOrCreateInodePageCache(file.OpenedInode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
            fileMapping = new VmaFileMapping(file);
        }
        else
        {
            // MAP_PRIVATE file: shared inode cache as source + process-private shadow object
            sharedObj = MemoryObjects.GetOrCreateInodePageCache(file.OpenedInode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
            privateObj = MemoryObjects.CreatePrivateOverlay();
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
            SharedObject = sharedObj,
            PrivateObject = privateObj,
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
    ///     Add a pre-constructed VMA directly. Used by SysV SHM subsystem.
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
                vma.SharedObject.Release();
                vma.PrivateObject?.Release();
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
                    SharedObject = vma.SharedObject,
                    PrivateObject = SharePrivateObjectForSplit(vma),
                    ViewPageOffset = vma.ViewPageOffset + (tailStart - vma.Start) / LinuxConstants.PageSize
                };
                vma.SharedObject.AddRef();

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
            false,
            true,
            true);
    }

    public int Mprotect(uint addr, uint len, Protection prot, Engine engine, out bool resetCodeCacheRange)
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
            resetCodeCacheRange |= ((oldPerms ^ prot) & Protection.Exec) != 0;

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
                SharedObject = vma.SharedObject,
                PrivateObject = SharePrivateObjectForSplit(vma),
                ViewPageOffset = oldViewPageOffset + (uint)midDiff / LinuxConstants.PageSize
            };
            vma.SharedObject.AddRef();
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
                    SharedObject = vma.SharedObject,
                    PrivateObject = SharePrivateObjectForSplit(vma),
                    ViewPageOffset = oldViewPageOffset + (uint)rightDiff / LinuxConstants.PageSize
                };
                vma.SharedObject.AddRef();
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
                // Move PrivateObject ownership to reused VMA slot; otherwise mid.PrivateObject is leaked.
                var oldPrivate = vma.PrivateObject;
                vma.Start = mid.Start;
                vma.End = mid.End;
                vma.Perms = mid.Perms;
                vma.FileMapping = mid.FileMapping;
                vma.Offset = mid.Offset;
                vma.FileBackingLength = mid.FileBackingLength;
                vma.Name = mid.Name;
                vma.ViewPageOffset = mid.ViewPageOffset;
                vma.SharedObject = mid.SharedObject;
                vma.PrivateObject = mid.PrivateObject;
                mid.PrivateObject = null;
                mid.FileMapping = null;
                // Drop the temporary extra ref held by mid.
                mid.SharedObject.Release();
                oldPrivate?.Release();

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

        ReprotectNativeMappings(engine, addr, len, prot, resetCodeCacheRange);

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
                false,
                true,
                true);
            vma.SharedObject.Release();
            vma.PrivateObject?.Release();
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
                // The next possible spot is right after this colliding VMA.
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

        var pages = ExternalPages.SnapshotMappedPages();
        if (pages.Count == 0) return 0;

        var sorted = pages.ToArray();
        Array.Sort(sorted);

        var dropped = 0;
        foreach (var pageAddr in sorted)
        {
            if (dropped >= maxPages) break;
            if (!ExternalPages.TryGet(pageAddr, out _)) continue;

            var vma = FindVMA(pageAddr);
            if (vma == null) continue;

            var pageIndex = vma.ViewPageOffset + (pageAddr - vma.Start) / LinuxConstants.PageSize;
            if (engine.IsDirty(pageAddr)) continue;
            if (vma.PrivateObject != null && vma.PrivateObject.PeekPage(pageIndex) != IntPtr.Zero) continue;
            if (!vma.SharedObject.IsRecoverableWithoutSwap) continue;
            if (vma.SharedObject.IsDirty(pageIndex)) continue;

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
        var result = MemoryPressureCoordinator.TryRelieveFault(
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

    private static bool IsPrivateVma(VMA vma)
    {
        return (vma.Flags & MapFlags.Private) != 0 && vma.PrivateObject != null;
    }

    public void CaptureDirtyPrivatePages(Engine engine)
    {
        foreach (var vma in _vmas)
            CaptureDirtyPrivatePages(engine, vma.Start, vma.End);
    }

    public void CaptureDirtyPrivatePages(Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (rangeStart >= rangeEnd) return;
        foreach (var vma in FindVMAsInRange(rangeStart, rangeEnd))
        {
            if (!IsPrivateVma(vma)) continue;
            var captureStart = Math.Max(vma.Start, rangeStart) & LinuxConstants.PageMask;
            var captureEnd = (Math.Min(vma.End, rangeEnd) + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
            for (var page = captureStart; page < captureEnd; page += LinuxConstants.PageSize)
            {
                if (!engine.IsDirty(page)) continue;
                if (!ExternalPages.TryGet(page, out var mappedPtr)) continue;
                var pageIndex = vma.ViewPageOffset + (page - vma.Start) / LinuxConstants.PageSize;
                var privatePtr = vma.PrivateObject!.PeekPage(pageIndex);
                if (privatePtr == IntPtr.Zero || privatePtr != mappedPtr) continue;
                vma.PrivateObject.MarkDirty(pageIndex);
            }
        }
    }

    private FaultResult ResolveAnonymousSharedSourceForRead(VMA vma, uint pageIndex, out IntPtr pagePtr)
    {
        pagePtr = vma.SharedObject.GetPage(pageIndex);
        if (pagePtr != IntPtr.Zero)
            return FaultResult.Handled;

        pagePtr = ZeroPageProvider.GetPointer();
        return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Oom;
    }

    private bool TryAllocatePrivateCopyFromSource(
        Engine engine,
        uint pageStart,
        IntPtr sourcePage,
        string pressureSource,
        out IntPtr privatePage)
    {
        privatePage = IntPtr.Zero;
        if (!ExternalPageManager.TryAllocateExternalPageStrict(out privatePage, AllocationClass.Cow,
                AllocationSource.CowFirstPrivate))
            if (!TryRelieveFaultMemoryPressure(engine, pageStart, pressureSource) ||
                !ExternalPageManager.TryAllocateExternalPageStrict(out privatePage, AllocationClass.Cow,
                    AllocationSource.CowFirstPrivate))
                return false;

        unsafe
        {
            Buffer.MemoryCopy((void*)sourcePage, (void*)privatePage,
                LinuxConstants.PageSize, LinuxConstants.PageSize);
        }

        return true;
    }

    private static int GetPageReadLength(VMA vma, long vmaRelativeOffset)
    {
        if (vma.FileBackingLength <= 0)
            return LinuxConstants.PageSize;

        var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;
        if (remainingBackingBytes <= 0)
            return 0;
        if (remainingBackingBytes < LinuxConstants.PageSize)
            return (int)remainingBackingBytes;
        return LinuxConstants.PageSize;
    }

    private static bool WantsWritableMappedFilePage(VMA vma)
    {
        return vma.File != null &&
               (vma.Flags & MapFlags.Shared) != 0 &&
               (vma.Perms & Protection.Write) != 0;
    }

    private FaultResult ResolveSharedBackingPage(
        VMA vma,
        uint pageIndex,
        long vmaRelativeOffset,
        long absoluteFileOffset,
        bool preferWritableMappedPage,
        Engine engine,
        out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        if (vma.File == null && vma.SharedObject.Role == MemoryObjectRole.AnonSharedSourceZeroFill)
            return ResolveAnonymousSharedSourceForRead(vma, pageIndex, out pagePtr);

        if (vma.File != null && vmaRelativeOffset >= vma.FileBackingLength)
            return FaultResult.BusError;

        if (vma.File != null &&
            TryResolveMappedFilePage(vma, pageIndex, absoluteFileOffset, preferWritableMappedPage, out pagePtr))
            return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Segv;

        var strictQuota = vma.File == null;
        var allocationClass = strictQuota ? AllocationClass.Anonymous : AllocationClass.PageCache;
        pagePtr = vma.SharedObject.GetOrCreatePage(pageIndex, ptr =>
        {
            if (vma.File == null)
            {
                unsafe
                {
                    new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                }

                return true;
            }

            unsafe
            {
                Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                var req = new PageIoRequest(pageIndex, absoluteFileOffset, GetPageReadLength(vma, vmaRelativeOffset));
                var rc = vma.File.OpenedInode!.ReadPage(vma.File, req, buf);
                if (rc < 0) return false;
            }

            return true;
        }, out _, strictQuota, allocationClass, strictQuota ? AllocationSource.AnonFault : AllocationSource.Unknown);

        if (pagePtr != IntPtr.Zero)
            return FaultResult.Handled;

        if (!strictQuota)
            return FaultResult.Segv;

        if (!TryRelieveFaultMemoryPressure(engine, (uint)(vma.Start + vmaRelativeOffset), "SharedBackingFault"))
            return FaultResult.Oom;

        pagePtr = vma.SharedObject.GetOrCreatePage(pageIndex, ptr =>
        {
            unsafe
            {
                new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
            }

            return true;
        }, out _, true, AllocationClass.Anonymous, AllocationSource.AnonFault);
        return pagePtr != IntPtr.Zero ? FaultResult.Handled : FaultResult.Oom;
    }

    private FaultResult EnsureExternalMapping(uint pageStart, IntPtr pagePtr, byte perms, Engine engine)
    {
        var hasCurrent = ExternalPages.TryGet(pageStart, out var mappedPtr);
        if (hasCurrent && mappedPtr == pagePtr)
        {
            engine.MemMap(pageStart, LinuxConstants.PageSize, perms);
            return FaultResult.Handled;
        }

        if (hasCurrent)
            ExternalPages.Release(pageStart);

        if (!ExternalPages.AddMapping(pageStart, pagePtr, out var addedRef))
            return FaultResult.Segv;
        if (!engine.MapExternalPage(pageStart, pagePtr, perms))
        {
            if (addedRef) ExternalPages.Release(pageStart);
            return FaultResult.Segv;
        }

        return FaultResult.Handled;
    }

    private FaultResult EnsureWritableExistingPrivatePage(
        MemoryObject privateObject,
        uint pageStart,
        uint pageIndex,
        IntPtr existingPrivate,
        byte perms,
        Engine engine)
    {
        var hasCurrentMapping = ExternalPages.TryGet(pageStart, out var mappedPtr);
        var mapsExistingPrivate = hasCurrentMapping && mappedPtr == existingPrivate;
        var nonOwnerRefs = ExternalPageManager.GetRefCount(existingPrivate) - 1 - (mapsExistingPrivate ? 1 : 0);
        if (nonOwnerRefs <= 0)
        {
            privateObject.MarkDirty(pageIndex);
            return EnsureExternalMapping(pageStart, existingPrivate, perms, engine);
        }

        if (!ExternalPageManager.TryAllocateExternalPageStrict(out var replacementPage, AllocationClass.Cow,
                AllocationSource.CowReplacePrivate))
            if (!TryRelieveFaultMemoryPressure(engine, pageStart, "CowReplacePrivate") ||
                !ExternalPageManager.TryAllocateExternalPageStrict(out replacementPage, AllocationClass.Cow,
                    AllocationSource.CowReplacePrivate))
                return FaultResult.Oom;

        Interlocked.Increment(ref _cowAllocReplaceCount);
        unsafe
        {
            Buffer.MemoryCopy((void*)existingPrivate, (void*)replacementPage,
                LinuxConstants.PageSize, LinuxConstants.PageSize);
        }

        privateObject.SetPage(pageIndex, replacementPage);
        privateObject.MarkDirty(pageIndex);
        ExternalPageManager.ReleasePtr(existingPrivate);
        return EnsureExternalMapping(pageStart, replacementPage, perms, engine);
    }

    private FaultResult ResolveSharedMappingFault(
        VMA vma,
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

        var vmaRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
        var absoluteFileOffset = vma.Offset + vmaRelativeOffset;
        var sharedSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
            WantsWritableMappedFilePage(vma), engine, out var pagePtr);
        if (sharedSourceResult != FaultResult.Handled)
            return sharedSourceResult;

        var tempPerms = vma.Perms | Protection.Write;
        var mappingResult = EnsureExternalMapping(pageStart, pagePtr, (byte)tempPerms, engine);
        if (mappingResult != FaultResult.Handled)
            return mappingResult;

        if (tempPerms != vma.Perms)
            engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);

        return FaultResult.Handled;
    }

    private FaultResult ResolvePrivateFault(VMA vma, uint pageStart, uint pageIndex, bool isWrite, Engine engine)
    {
        var privateObject = vma.PrivateObject!;
        var vmaRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
        var absoluteFileOffset = vma.Offset + vmaRelativeOffset;
        var readPerms = (byte)(vma.Perms & ~Protection.Write);

        var existingPrivate = privateObject.GetPage(pageIndex);
        if (!isWrite)
        {
            if (existingPrivate != IntPtr.Zero)
                return EnsureExternalMapping(pageStart, existingPrivate, readPerms, engine);

            var readSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
                false, engine, out var sharedPage);
            if (readSourceResult != FaultResult.Handled)
                return readSourceResult;
            return EnsureExternalMapping(pageStart, sharedPage, readPerms, engine);
        }

        if (existingPrivate != IntPtr.Zero)
            return EnsureWritableExistingPrivatePage(privateObject, pageStart, pageIndex, existingPrivate,
                (byte)vma.Perms, engine);

        var sharedSourceResult = ResolveSharedBackingPage(vma, pageIndex, vmaRelativeOffset, absoluteFileOffset,
            false, engine, out var sourcePage);
        if (sharedSourceResult != FaultResult.Handled)
            return sharedSourceResult;

        if (!TryAllocatePrivateCopyFromSource(engine, pageStart, sourcePage, "CowFirstPrivate", out sourcePage))
            return FaultResult.Oom;

        Interlocked.Increment(ref _cowAllocFirstCount);
        privateObject.SetPage(pageIndex, sourcePage);
        privateObject.MarkDirty(pageIndex);
        return EnsureExternalMapping(pageStart, sourcePage, (byte)vma.Perms, engine);
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
        var pageIndex = vma.ViewPageOffset + (pageStart - vma.Start) / LinuxConstants.PageSize;


        if (IsPrivateVma(vma))
            return ResolvePrivateFault(vma, pageStart, pageIndex, isWrite, engine);

        return ResolveSharedMappingFault(vma, addr, pageStart, pageIndex, isWrite, engine);
    }

    public bool HandleFault(uint addr, bool isWrite, Engine engine)
    {
        return HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
    }

    private static bool TryResolveMappedFilePage(VMA vma, uint pageIndex, long absoluteFileOffset, bool writable,
        out IntPtr pagePtr)
    {
        pagePtr = IntPtr.Zero;
        var inode = vma.File?.OpenedInode;
        if (inode == null) return false;
        if (!inode.TryAcquireMappedPageHandle(vma.File, pageIndex, absoluteFileOffset, writable, out var pageHandle))
            return false;
        if (pageHandle == null) return false;

        var mappedPtr = pageHandle.Pointer;
        if (mappedPtr == IntPtr.Zero)
        {
            pageHandle.Dispose();
            return false;
        }

        var existing = vma.SharedObject.PeekPage(pageIndex);
        if (existing != IntPtr.Zero)
        {
            pageHandle.Dispose();
            pagePtr = existing;
            return true;
        }

        ExternalPageManager.AddRefPtr(mappedPtr, pageHandle);
        var finalPtr = vma.SharedObject.SetPageIfAbsent(pageIndex, mappedPtr, out var inserted);
        if (!inserted)
            ExternalPageManager.ReleasePtr(mappedPtr);

        pagePtr = finalPtr;
        return pagePtr != IntPtr.Zero;
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

            var startPage = (ulong)(captureStart & LinuxConstants.PageMask);
            var endPageExclusive = ((ulong)captureEnd + LinuxConstants.PageOffsetMask) &
                                   LinuxConstants.PageMask;

            for (var page = startPage; page < endPageExclusive; page += LinuxConstants.PageSize)
            {
                var pageAddr = (uint)page;
                var vmaRelativeOffset = pageAddr - vma.Start;
                if (!engine.IsDirty(pageAddr)) continue;
                var pageIndex = vma.ViewPageOffset + vmaRelativeOffset / LinuxConstants.PageSize;
                vma.SharedObject.MarkDirty(pageIndex);
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
        var startPageIndex = vma.ViewPageOffset + (startPage - vma.Start) / LinuxConstants.PageSize;
        var endPageIndex = endPage <= startPage
            ? startPageIndex
            : vma.ViewPageOffset + (endPage - vma.Start) / LinuxConstants.PageSize - 1;

        for (var page = startPage; page < endPage; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.ViewPageOffset + (page - vma.Start) / LinuxConstants.PageSize;

            var isDirty = false;
            foreach (var engine in engines)
            {
                if (!engine.IsDirty(page)) continue;
                isDirty = true;
                break;
            }

            if (isDirty)
            {
                vma.SharedObject.MarkDirty(pageIndex);
                inode.SetPageDirty(pageIndex);
            }

            if (!vma.SharedObject.IsDirty(pageIndex)) continue;
            var pagePtr = vma.SharedObject.PeekPage(pageIndex);
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

            if (writeLen <= 0) continue;

            unsafe
            {
                ReadOnlySpan<byte> pageData = new((void*)pagePtr, LinuxConstants.PageSize);
                GlobalPageCacheManager.BeginWritebackPages();
                try
                {
                    if (inode.TryFlushMappedPage(vma.File, pageIndex))
                    {
                        vma.SharedObject.ClearDirty(pageIndex);
                    }
                    else
                    {
                        var rc = inode.WritePage(vma.File, new PageIoRequest(pageIndex, absoluteFileOffset, writeLen),
                            pageData, true);
                        if (rc == 0)
                            vma.SharedObject.ClearDirty(pageIndex);
                    }
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

    internal readonly record struct NativeRange(uint Start, uint Length);

    private readonly record struct CodeCacheResetEntry(long Sequence, NativeRange Range);
}