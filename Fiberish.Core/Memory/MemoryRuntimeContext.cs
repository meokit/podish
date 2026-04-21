using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class MemoryRuntimeContext : IDisposable
{
    private readonly Lock _shmGate = new();
    private long _aslrSequence;
    private bool _disposed;
    private long _nextSharedAnonymousBackingId;
    private SuperBlock? _shmSuperBlock;
    private ZeroInode? _zeroInode;

    public MemoryRuntimeContext()
        : this(HostMemoryMapGeometry.CreateCurrent())
    {
    }

    public MemoryRuntimeContext(HostMemoryMapGeometry hostMemoryMapGeometry)
    {
        HostMemoryMapGeometry = hostMemoryMapGeometry;
        BackingPagePool = new BackingPagePool(this);
        HostPages = new HostPageManager();
        AddressSpacePolicy = new AddressSpacePolicy();
        MemoryPressure = new MemoryPressureCoordinator(AddressSpacePolicy);
    }

    public HostMemoryMapGeometry HostMemoryMapGeometry { get; }
    public BackingPagePool BackingPagePool { get; }
    internal HostPageManager HostPages { get; }
    public AddressSpacePolicy AddressSpacePolicy { get; }
    public MemoryPressureCoordinator MemoryPressure { get; }
    public ulong? DeterministicAslrSeed { get; set; }

    public long MemoryQuotaBytes
    {
        get => BackingPagePool.MemoryQuotaBytes;
        set => BackingPagePool.MemoryQuotaBytes = value;
    }

    public long GetAllocatedBytes()
    {
        return BackingPagePool.GetAllocatedBytes();
    }

    public long GetCachedBytes()
    {
        return AddressSpacePolicy.GetTotalCachedPages() * LinuxConstants.PageSize;
    }

    public long GetTotalTrackedBytes()
    {
        return GetAllocatedBytes() + GetCachedBytes();
    }

    public IReadOnlyList<AllocationClassStat> GetAllocationClassStats()
    {
        return BackingPagePool.GetAllocationClassStats();
    }

    public string GetAllocationClassStatsSummary()
    {
        return BackingPagePool.GetAllocationClassStatsSummary();
    }

    public MemoryStatsSnapshot CaptureMemoryStats(SyscallManager? sm = null)
    {
        return MemoryStatsSnapshot.CreateForRuntime(this, sm);
    }

    internal byte[] CreateExecRandomBytes(int length, string? tag = null)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var buffer = new byte[length];
        FillExecRandomBytes(buffer, tag);
        return buffer;
    }

    internal void FillExecRandomBytes(Span<byte> buffer, string? tag = null)
    {
        if (buffer.Length == 0)
            return;

        if (DeterministicAslrSeed is not { } seed)
        {
            RandomNumberGenerator.Fill(buffer);
            return;
        }

        var sequence = (ulong)Interlocked.Increment(ref _aslrSequence);
        var tagBytes = string.IsNullOrEmpty(tag) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(tag);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(header[..8], seed);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..16], sequence);

        var written = 0;
        uint blockCounter = 0;
        while (written < buffer.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], blockCounter++);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], (uint)tagBytes.Length);
            var hashInput = new byte[header.Length + tagBytes.Length];
            header.CopyTo(hashInput);
            if (tagBytes.Length != 0)
                tagBytes.CopyTo(hashInput.AsSpan(header.Length));
            var hash = SHA256.HashData(hashInput);
            var toCopy = Math.Min(hash.Length, buffer.Length - written);
            hash.AsSpan(0, toCopy).CopyTo(buffer[written..]);
            written += toCopy;
        }
    }

    public HostPageRefStatsSnapshot CaptureHostPageRefStats()
    {
        return HostPages.CaptureStats();
    }

    public BackingPagePoolSegmentStatsSnapshot CaptureBackingPagePoolSegmentStats()
    {
        return BackingPagePool.CaptureSegmentStats();
    }

    internal SuperBlock GetOrCreateShmSuperBlock(DeviceNumberManager? deviceNumbers = null)
    {
        lock (_shmGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shmSuperBlock != null)
                return _shmSuperBlock;

            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static devMgr => new Tmpfs(devMgr),
                FactoryWithContext = static (devMgr, memoryContext) => new Tmpfs(devMgr, memoryContext)
            };
            var fileSystem = deviceNumbers != null
                ? fsType.CreateFileSystem(deviceNumbers, this)
                : fsType.CreateAnonymousFileSystem(this);
            var superBlock = fileSystem.ReadSuper(fsType, 0, "shm_mnt", null);
            _shmSuperBlock = superBlock;
            return superBlock;
        }
    }

    internal LinuxFile CreateSharedAnonymousMappingFile(uint length)
    {
        var name = $".map_shared_anon.{Interlocked.Increment(ref _nextSharedAnonymousBackingId)}";
        return CreateUnlinkedShmFile(name, length, 0x180, 0, 0, FileFlags.O_RDWR, null!,
            LinuxFile.ReferenceKind.MmapHold);
    }

    internal LinuxFile CreateSysVSharedMemoryFile(int shmid, uint length, int uid, int gid)
    {
        return CreateUnlinkedShmFile($".sysvshm.{shmid}", length, 0x1B6, uid, gid, FileFlags.O_RDWR, null!,
            LinuxFile.ReferenceKind.MmapHold);
    }

    internal LinuxFile CreateMemfdFile(string displayName, FileFlags fileFlags, int mode, int uid, int gid,
        bool allowSealing, bool executable, bool noExecSeal, Mount mount)
    {
        var internalName = $".memfd.{Interlocked.Increment(ref _nextSharedAnonymousBackingId)}";
        return CreateUnlinkedShmFile(internalName, 0, mode, uid, gid, fileFlags, mount,
            LinuxFile.ReferenceKind.Normal,
            inode => inode.InitializeMemfd(displayName, allowSealing, executable, noExecSeal));
    }

    internal AddressSpace AcquireZeroMappingRef()
    {
        lock (_shmGate)
        {
            EnsureZeroInodeCreated();
            return _zeroInode!.AcquireMappingRef();
        }
    }

    internal bool IsZeroAddressSpace(AddressSpace? mapping)
    {
        lock (_shmGate)
        {
            return mapping != null && ReferenceEquals(mapping, _zeroInode?.Mapping);
        }
    }

    internal bool IsZeroPageHostReadOnlyProtected
    {
        get
        {
            lock (_shmGate)
            {
                return _zeroInode?.IsHostReadOnlyProtected ?? false;
            }
        }
    }

    internal PooledSegmentAllocationKind? ZeroPageAllocationKind
    {
        get
        {
            lock (_shmGate)
            {
                return _zeroInode?.AllocationKind;
            }
        }
    }

    internal nuint ZeroPageReservationSizeBytes
    {
        get
        {
            lock (_shmGate)
            {
                return _zeroInode?.ReservationSizeBytes ?? 0;
            }
        }
    }

    internal IntPtr AcquireZeroMappingPage(uint pageIndex)
    {
        lock (_shmGate)
        {
            EnsureZeroInodeCreated();
            return _zeroInode!.AcquireMappingPage(null, pageIndex, 0, PageCacheAccessMode.Read, 0, false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_shmGate)
        {
            _zeroInode?.Dispose();
            _zeroInode = null;
            _shmSuperBlock = null;
        }

        AddressSpacePolicy.Dispose();
        HostPages.Dispose();
        BackingPagePool.Dispose();
    }

    private void EnsureZeroInodeCreated()
    {
        if (_zeroInode != null)
            return;

        _zeroInode = new ZeroInode(this);
    }

    private LinuxFile CreateUnlinkedShmFile(string name, uint length, int mode, int uid, int gid, FileFlags fileFlags,
        Mount mount, LinuxFile.ReferenceKind referenceKind, Action<TmpfsInode>? configureTmpfsInode = null)
    {
        lock (_shmGate)
        {
            var superBlock = GetOrCreateShmSuperBlock();
            var root = superBlock.Root;
            var fsName = FsName.FromString(name);
            var dentry = new Dentry(fsName, null, root, superBlock);
            var createRc = root.Inode!.Create(dentry, mode, uid, gid);
            if (createRc < 0)
                throw new InvalidOperationException($"Failed to create shm_mnt backing inode '{name}': rc={createRc}.");

            try
            {
                if (dentry.Inode is TmpfsInode tmpfsInode)
                    configureTmpfsInode?.Invoke(tmpfsInode);

                if (length != 0)
                {
                    var truncateRc = dentry.Inode!.Truncate(length);
                    if (truncateRc < 0)
                        throw new InvalidOperationException(
                            $"Failed to size shm_mnt backing inode '{name}' to {length} bytes: rc={truncateRc}.");
                }

                var file = new LinuxFile(dentry, fileFlags, mount, referenceKind);
                var unlinkRc = root.Inode.Unlink(fsName);
                if (unlinkRc < 0)
                    file.IsTmpFile = true;

                return file;
            }
            catch
            {
                _ = root.Inode.Unlink(fsName);
                throw;
            }
        }
    }
}

internal sealed class ZeroInode : MappingBackedInode, IDisposable
{
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly Lock _zeroPageGate = new();
    private IntPtr _sharedZeroPagePtr;
    private PooledSegmentMemoryReservation _sharedZeroPageReservation;
    private bool _sharedZeroPageHostProtected;

    public ZeroInode(MemoryRuntimeContext memoryContext)
    {
        _memoryContext = memoryContext;
        Mapping = new AddressSpace(memoryContext, AddressSpaceKind.Zero);
        Ino = 0;
        Type = InodeType.File;
        Mode = 0x124;
        Size = LinuxConstants.PageSize;
    }

    protected override AddressSpaceKind MappingKind => AddressSpaceKind.Zero;
    protected override AddressSpacePolicy.AddressSpaceCacheClass? MappingCacheClass => null;
    internal bool IsHostReadOnlyProtected => _sharedZeroPageHostProtected;
    internal PooledSegmentAllocationKind? AllocationKind =>
        _sharedZeroPageReservation.IsAllocated ? _sharedZeroPageReservation.AllocationKind : null;
    internal nuint ReservationSizeBytes => _sharedZeroPageReservation.Size;

    internal override InodePageRecord? TryCreateIntrinsicMappingPage(uint pageIndex)
    {
        lock (_zeroPageGate)
        {
            if (_sharedZeroPagePtr == IntPtr.Zero)
            {
                var zeroPageBytes = checked((nuint)Math.Max(
                    LinuxConstants.PageSize,
                    _memoryContext.HostMemoryMapGeometry.HostPageSize));
                var reservation = PooledSegmentMemory.Allocate(zeroPageBytes);
                if (!reservation.IsAllocated)
                    return null;

                try
                {
                    unsafe
                    {
                        if (!reservation.IsZeroInitialized)
                            new Span<byte>((void*)reservation.BasePtr, checked((int)reservation.Size)).Clear();
                    }

                    var zeroPagePtr = (IntPtr)reservation.BasePtr;
                    var hostProtected = false;
                    if (PooledSegmentMemory.SupportsReadOnlyProtection)
                    {
                        if (!PooledSegmentMemory.TryProtectReadOnly(reservation, reservation.BasePtr, reservation.Size))
                        {
                            throw new InvalidOperationException(
                                $"Failed to mark shared zero page 0x{reservation.BasePtr.ToInt64():X} read-only: " +
                                $"osError={Marshal.GetLastPInvokeError()}.");
                        }

                        hostProtected = true;
                    }

                    _sharedZeroPageReservation = reservation;
                    _sharedZeroPagePtr = zeroPagePtr;
                    _sharedZeroPageHostProtected = hostProtected;
                }
                catch
                {
                    PooledSegmentMemory.Free(reservation);
                    throw;
                }
            }

            _memoryContext.HostPages.GetOrCreate(_sharedZeroPagePtr, HostPageKind.Zero);

            return new InodePageRecord
            {
                PageIndex = pageIndex,
                Ptr = _sharedZeroPagePtr,
                BackingKind = FilePageBackingKind.ZeroSharedPage
            };
        }
    }

    public void Dispose()
    {
        Mapping?.Release();
        Mapping = null;

        lock (_zeroPageGate)
        {
            if (_sharedZeroPagePtr == IntPtr.Zero)
                return;

            PooledSegmentMemory.Free(_sharedZeroPageReservation);
            _sharedZeroPageReservation = default;
            _sharedZeroPagePtr = IntPtr.Zero;
            _sharedZeroPageHostProtected = false;
        }
    }
}
