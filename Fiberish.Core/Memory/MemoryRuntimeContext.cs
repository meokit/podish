using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class MemoryRuntimeContext
{
    private readonly Lock _shmGate = new();
    private long _nextSharedAnonymousBackingId;
    private SuperBlock? _shmSuperBlock;
    private LinuxFile? _zeroBackingFile;
    private AddressSpace? _zeroAddressSpace;

    public MemoryRuntimeContext()
        : this(HostMemoryMapGeometry.CreateCurrent())
    {
    }

    public MemoryRuntimeContext(HostMemoryMapGeometry hostMemoryMapGeometry)
    {
        HostMemoryMapGeometry = hostMemoryMapGeometry;
    }

    public static MemoryRuntimeContext Default { get; } = new();

    public HostMemoryMapGeometry HostMemoryMapGeometry { get; }

    internal SuperBlock GetOrCreateShmSuperBlock(DeviceNumberManager? deviceNumbers = null)
    {
        lock (_shmGate)
        {
            if (_shmSuperBlock != null)
                return _shmSuperBlock;

            var fsType = new FileSystemType { Name = "tmpfs", Factory = static devMgr => new Tmpfs(devMgr) };
            var fileSystem = deviceNumbers != null
                ? fsType.CreateFileSystem(deviceNumbers)
                : fsType.CreateAnonymousFileSystem();
            var superBlock = fileSystem.ReadSuper(fsType, 0, "shm_mnt", null);
            superBlock.MemoryContext = this;
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
        return CreateUnlinkedShmFile(displayName, 0, mode, uid, gid, fileFlags, mount,
            LinuxFile.ReferenceKind.Normal, inode => inode.InitializeMemfd(allowSealing, executable, noExecSeal));
    }

    internal AddressSpace AcquireZeroAddressSpace()
    {
        lock (_shmGate)
        {
            EnsureZeroBackingCreated();
            _zeroAddressSpace!.AddRef();
            return _zeroAddressSpace;
        }
    }

    internal bool IsZeroAddressSpace(AddressSpace? mapping)
    {
        lock (_shmGate)
        {
            return mapping != null && ReferenceEquals(mapping, _zeroAddressSpace);
        }
    }

    internal IntPtr GetOrCreateZeroPage(uint pageIndex)
    {
        lock (_shmGate)
        {
            EnsureZeroBackingCreated();
            var zeroPtr = ZeroPageProvider.GetPointer();
            if (zeroPtr == IntPtr.Zero)
                return IntPtr.Zero;

            return _zeroAddressSpace!.SetPageIfAbsent(pageIndex, zeroPtr, out _);
        }
    }

    private void EnsureZeroBackingCreated()
    {
        if (_zeroAddressSpace != null)
            return;

        _zeroBackingFile = CreateUnlinkedShmFile(".zero_page", LinuxConstants.PageSize, 0x100, 0, 0,
            FileFlags.O_RDONLY, null!, LinuxFile.ReferenceKind.MmapHold);
        _zeroAddressSpace = new AddressSpace(AddressSpaceKind.Zero);
        if (_zeroBackingFile.OpenedInode != null)
        {
            _zeroBackingFile.OpenedInode.Mapping = _zeroAddressSpace;
            _zeroBackingFile.OpenedInode.MappingManager = null;
        }
    }

    private LinuxFile CreateUnlinkedShmFile(string name, uint length, int mode, int uid, int gid, FileFlags fileFlags,
        Mount mount, LinuxFile.ReferenceKind referenceKind, Action<TmpfsInode>? configureTmpfsInode = null)
    {
        lock (_shmGate)
        {
            var superBlock = GetOrCreateShmSuperBlock();
            var root = superBlock.Root;
            var dentry = new Dentry(name, null, root, superBlock);
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
                var unlinkRc = root.Inode.Unlink(name);
                if (unlinkRc < 0)
                    file.IsTmpFile = true;

                return file;
            }
            catch
            {
                _ = root.Inode.Unlink(name);
                throw;
            }
        }
    }
}
