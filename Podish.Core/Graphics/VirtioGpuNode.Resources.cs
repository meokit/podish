using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Podish.Core.Graphics;

internal sealed partial class VirtioGpuNode
{
    private sealed class Resource
    {
        private readonly Context _owner;
        private int _refs = 1;
        private volatile Fence? _lastFence;

        internal Resource(Context context, uint id, ulong size, int fd, VirglResourceShape? virgl = null)
        {
            _owner = context;
            context.Retain();
            Id = id;
            Size = size;
            HostFd = fd;
            Virgl = virgl;
        }

        internal uint Id { get; }
        internal ulong Size { get; }
        internal ulong MappedSize => (Size + LinuxConstants.PageOffsetMask) & ~(ulong)LinuxConstants.PageOffsetMask;
        internal int HostFd { get; private set; }
        internal VirglResourceShape? Virgl { get; }
        internal bool IsVirgl => _owner.IsVirgl;
        internal ulong HostSize { get; private set; }
        internal LinuxFile? Backing { get; private set; }
        internal Fence? LastFence
        {
            get => _lastFence;
            set => _lastFence = value;
        }
        internal bool IsOwnedBy(Context context) => _owner == context;
        internal nint GetVirglMetalTexture() => _owner.Renderer.ExportVirglTexture(_owner.Id, Id);
        internal bool CanExportVirglTexture => _owner.Renderer.CanExportVirglTexture;

        internal bool CreateBacking(SyscallManager syscalls)
        {
            // Darwin shm_open descriptors support fstat and mmap, but reject lseek.
            var length = _owner.Renderer.GetSharedFdSize(HostFd);
            if (length < 0 || (ulong)length < MappedSize) return false;
            HostSize = (ulong)length;
            var pointer = BlobMemory.Map(HostFd, (nuint)MappedSize);
            if (pointer == -1) return false;
            var inode = new BlobMemory(syscalls.MemfdSuperBlock, Id, MappedSize, pointer);
            Backing = MakeFile(syscalls, inode, "anon_inode:[venus-blob]", FileFlags.O_RDWR);
            if (_owner.IsVirgl)
            {
                VenusRenderer.Close(HostFd);
                HostFd = -1;
            }
            return true;
        }

        internal void Retain() => Interlocked.Increment(ref _refs);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            _owner.Renderer.DestroyResource(_owner, Id);
            if (HostFd >= 0) VenusRenderer.Close(HostFd);
            Backing?.Close();
            _owner.Release();
        }
    }

    /// <summary>Expose the renderer's MAP_SHARED pages directly to the emulated MMU.</summary>
    private sealed class BlobMemory : MappingBackedInode
    {
        private readonly nint _pointer;
        private int _mapReferences = 1;

        internal BlobMemory(SuperBlock superBlock, uint id, ulong size, nint pointer)
        {
            SuperBlock = superBlock;
            Ino = (1UL << 34) + id;
            Type = InodeType.File;
            Mode = 0x180;
            Size = size;
            _pointer = pointer;
            AnonymousInodeLifecycle.Initialize(this, "Venus blob");
        }

        public override bool SupportsMmap => true;
        internal override bool PreferHostMappedMappingPage(PageCacheAccessMode accessMode) => true;

        public override bool TryAcquireMappedPageHandle(LinuxFile? file, long pageIndex, long offset,
            bool writable, out BackingPageHandle handle)
        {
            handle = default;
            if (offset < 0 || (ulong)offset >= Size) return false;
            Interlocked.Increment(ref _mapReferences);
            handle = BackingPageHandle.CreateOwned(_pointer + (nint)offset, this, 0);
            return true;
        }

        public override unsafe int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset)
        {
            if (offset < 0) return Invalid;
            if ((ulong)offset >= Size) return 0;
            var count = (int)Math.Min((ulong)buffer.Length, Size - (ulong)offset);
            new ReadOnlySpan<byte>((void*)(_pointer + (nint)offset), count).CopyTo(buffer);
            return count;
        }

        public override bool TryFlushMappedPage(LinuxFile? file, long pageIndex,
            PageWritebackMode mode = PageWritebackMode.Durable) => true;

        protected internal override void ReleaseMappedPageHandle(long releaseToken) => ReleaseMap();

        public override void Release(LinuxFile file)
        {
            base.Release(file);
            AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "Venus blob close");
        }

        protected override void OnFinalizeDelete()
        {
            base.OnFinalizeDelete();
            ReleaseMap();
        }

        private void ReleaseMap()
        {
            if (Interlocked.Decrement(ref _mapReferences) == 0)
                Unmap(_pointer, (nuint)Size);
        }

        internal static nint Map(int fd, nuint length) => Mmap(0, length, 3, 1, fd, 0); // RW, MAP_SHARED

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mmap")]
        private static extern nint Mmap(nint address, nuint size, int protection, int flags, int fd, long offset);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "munmap")]
        private static extern int Unmap(nint address, nuint size);
    }

    private static LinuxFile MakeFile(SyscallManager syscalls, Inode inode, string name, FileFlags flags) =>
        new(new Dentry(name, inode, null, inode.SuperBlock), flags, syscalls.AnonMount);

    private static int ExportPrime(OpenFile open, FiberTask task, uint arg, Span<byte> data)
    {
        var flags = U32(data, 4);
        const uint closeOnExec = 0x80000, readWrite = 2;
        if ((flags & ~(closeOnExec | readWrite)) != 0) return Invalid;
        if (!open.Handles.TryGetValue(U32(data, 0), out var entry)) return Missing;
        if (entry.Resource.IsVirgl ? !entry.Resource.CanExportVirglTexture : entry.Resource.Backing == null)
            return -(int)Errno.EOPNOTSUPP;

        var syscalls = task.Process.Syscalls;
        var prime = new PrimeInode(syscalls.MemfdSuperBlock, entry.Resource);
        var name = entry.Resource.IsVirgl ? "dmabuf:virgl" : "dmabuf:venus";
        var file = MakeFile(syscalls, prime, name, (flags & readWrite) != 0 ? FileFlags.O_RDWR : FileFlags.O_RDONLY);
        var fd = syscalls.AllocFD(file, closeOnExec: (flags & closeOnExec) != 0, task: task);
        return WriteFd(task, arg, data, 8, fd);
    }

    private static int ImportPrime(OpenFile open, FiberTask task, uint arg, Span<byte> data)
    {
        if (U32(data, 4) != 0) return Invalid;
        var file = task.Process.Syscalls.GetFD((int)U32(data, 8), task);
        if (file == null) return -(int)Errno.EBADF;
        if (file.OpenedInode is not PrimeInode prime) return Invalid;
        var resource = prime.Resource;
        if (open.Context is { } owner && owner.IsVirgl != resource.IsVirgl) return Invalid;
        if (resource.IsVirgl && open.Context == null) return Invalid;
        foreach (var (existing, entry) in open.Handles)
        {
            if (entry.Resource != resource) continue;
            Put(data, 0, existing);
            return Write(task.CPU, arg, data) ? 0 : Fault;
        }

        Context? imported = null;
        if (open.Context is { } context && !resource.IsOwnedBy(context))
        {
            if (!context.Renderer.ImportResource(context, resource.Id, resource.HostFd, resource.HostSize))
                return Invalid;
            imported = context;
        }

        var handle = open.NextHandle++;
        Put(data, 0, handle);
        if (!Write(task.CPU, arg, data))
        {
            if (imported != null) imported.Renderer.DetachResource(imported, resource.Id);
            return Fault;
        }
        resource.Retain();
        open.Handles.Add(handle, new Handle(resource, imported));
        return 0;
    }

    internal static bool IsVenusPrimeFile(LinuxFile file) => file.OpenedInode is PrimeInode { Resource.IsVirgl: false };
    internal static bool IsVirglPrimeFile(LinuxFile file) => file.OpenedInode is PrimeInode { Resource.IsVirgl: true };

    internal static VenusScanoutBuffer? GetLinearPrimeBuffer(LinuxFile file, uint width, uint height,
        uint pitch, uint offset, uint format)
    {
        if (file.OpenedInode is not PrimeInode { Resource: { IsVirgl: false, HostFd: >= 0, Backing: not null } resource })
            return null;

        resource.Retain();
        return new VenusScanoutBuffer(width, height, pitch, offset, format,
            resource.HostFd, resource.HostSize, resource.Release);
    }

    internal static VirglScanoutBuffer? GetVirglPrimeBuffer(LinuxFile file, uint width, uint height,
        uint pitch, uint offset, uint format)
    {
        if (offset != 0 || file.OpenedInode is not PrimeInode { Resource: { Virgl: { } shape } resource } ||
            !shape.IsTexturePlane(width, height, pitch) || !resource.CanExportVirglTexture)
            return null;

        resource.Retain();
        return new VirglScanoutBuffer(width, height, pitch, format, resource.GetVirglMetalTexture, resource.Release);
    }

    private sealed class PrimeInode : Inode, ITaskWaitSource
    {
        internal PrimeInode(SuperBlock superBlock, Resource resource)
        {
            Resource = resource;
            SuperBlock = superBlock;
            Ino = (1UL << 35) + resource.Id;
            Type = InodeType.File;
            Mode = 0x180;
            Size = resource.Size;
            AnonymousInodeLifecycle.Initialize(this, "Venus PRIME export");
        }

        internal Resource Resource { get; }
        public override bool SupportsMmap => true;

        public override void Open(LinuxFile file)
        {
            base.Open(file);
            Resource.Retain();
        }

        public override void Release(LinuxFile file)
        {
            Resource.Release();
            base.Release(file);
            AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "Venus PRIME close");
        }

        public override int ResolveMmapBacking(LinuxFile file, uint length, ref long offset, out LinuxFile backing)
        {
            backing = Resource.Backing ?? file;
            return Resource.Backing != null && offset >= 0 && (ulong)offset + length <= Resource.MappedSize ? 0 : Invalid;
        }

        public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events) =>
            RegisterWaitHandle(file, task, callback, events) != null;

        public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events) =>
            Resource.LastFence is { } fence
                ? QueueReadinessRegistration.RegisterHandle(callback, task, events,
                    new QueueReadinessWatch(PollEvents.POLLIN | PollEvents.POLLOUT, () => fence.Signaled, fence.Ready))
                : null;

        public override short Poll(LinuxFile file, short events) =>
            Resource.LastFence is { Signaled: false } ? (short)0 : (short)(events & (PollEvents.POLLIN | PollEvents.POLLOUT));

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
        {
            if (request is not (0x40086200 or 0xc0086202 or 0x40086203)) return -(int)Errno.ENOTTY;
            Span<byte> data = stackalloc byte[8];
            if (!Read(task.CPU, arg, data)) return Fault;

            if (request == 0x40086200) // DMA_BUF_IOCTL_SYNC
            {
                var flags = U64(data, 0);
                if ((flags & ~7ul) != 0 || (flags & 3) == 0) return Invalid;
                return Resource.LastFence?.Wait() ?? 0;
            }

            if (U32(data, 0) is 0 or > 3) return Invalid;
            if (request == 0xc0086202) // DMA_BUF_IOCTL_EXPORT_SYNC_FILE
                return WriteFd(task, arg, data, 4, CreateFenceFd(task, Resource.LastFence));

            var input = task.Process.Syscalls.GetFD((int)U32(data, 4), task);
            if (input?.OpenedInode is not FenceInode fenceFile) return Invalid;
            var acquire = fenceFile.Fence;
            if (acquire.Signaled) return 0;
            if (Resource.LastFence is { Signaled: false } previous && previous != acquire)
            {
                if (previous.TryMerge(acquire, out var merged))
                    acquire = merged;
                else
                {
                    var rc = previous.Wait();
                    if (rc != 0) return rc;
                }
            }
            Resource.LastFence = acquire;
            return 0;
        }
    }
}
