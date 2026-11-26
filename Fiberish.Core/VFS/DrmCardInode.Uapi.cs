using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.VFS;

public sealed partial class DrmCardInode
{
    private const uint GetMagic = 0x80046402, SetClientCap = 0x4010640d;
    private const uint PrimeExport = 0xc00c642d, PrimeImport = 0xc00c642e;
    private const uint ImportGbmBo = 0xc01c64f2;
    private const uint SetCursor = 0xc01c64a3, GetProperty = 0xc04064aa, SetConnectorProperty = 0xc01064ab;
    private const uint GetPlaneResources = 0xc00c64b5, GetPlaneResourcesAArch64 = 0xc01064b5, GetPlane = 0xc02064b6;
    private const uint CreateLease = 0xc01864c6, ListLessees = 0xc01064c7;
    private const uint PrimaryPlane = 4, PlaneTypeProperty = 5, DpmsProperty = 6;
    private const uint PrimaryPlaneType = 1;
    private const uint PrimeCloseOnExec = 0x80000, PrimeReadWrite = 2;
    private static readonly string[] PlaneTypeNames = ["Overlay", "Primary", "Cursor"];
    private static readonly string[] DpmsNames = ["On", "Standby", "Suspend", "Off"];
    private readonly HashSet<uint> _pendingMagic = [];
    private uint _nextMagic = 1;
    private uint _dpms;

    private int GetAuthenticationMagic(Session session, Span<byte> data)
    {
        if (session.Magic == 0)
        {
            session.Magic = _nextMagic++;
            _pendingMagic.Add(session.Magic);
        }
        Put(data, 0, session.Magic);
        return 0;
    }

    private int Authenticate(Session session, Span<byte> data)
    {
        if (_master != session) return -(int)Errno.EACCES;
        return _pendingMagic.Remove(U32(data, 0)) ? 0 : -(int)Errno.EINVAL;
    }

    private static int SetClientCapability(Session session, ReadOnlySpan<byte> data)
    {
        var value = U64(data, 8);
        if (U64(data, 0) != 2 || value > 1) return -(int)Errno.EINVAL;
        session.UniversalPlanes = value != 0;
        return 0;
    }

    private static int PlaneResources(Session session, Engine cpu, Span<byte> data)
    {
        if (session.UniversalPlanes)
            CopyId(cpu, U64(data, 0), U32(data, 8), PrimaryPlane);
        Put(data, 8, session.UniversalPlanes ? 1u : 0u);
        return 0;
    }

    private int Plane(Session session, Engine cpu, Span<byte> data)
    {
        if (!session.UniversalPlanes || U32(data, 0) != PrimaryPlane) return -(int)Errno.ENOENT;
        if (U32(data, 20) >= 2)
        {
            Span<byte> formats = stackalloc byte[8];
            Put(formats, 0, Xrgb8888);
            Put(formats, 4, Argb8888);
            CopyOut(cpu, U64(data, 24), formats);
        }
        Put(data, 4, _scanout == 0 ? 0 : CrtcId);
        Put(data, 8, _scanout);
        Put(data, 12, 1);
        Put(data, 16, 0);
        Put(data, 20, 2);
        return 0;
    }

    private int ObjectProperties(Engine cpu, Span<byte> data)
    {
        switch (U32(data, 20), U32(data, 24))
        {
            case (PrimaryPlane, 0xeeeeeeee or 0):
                WritePropertyList(cpu, data, 0, 8, 16, PlaneTypeProperty, PrimaryPlaneType);
                break;
            case (ConnectorId, 0xc0c0c0c0 or 0):
                WritePropertyList(cpu, data, 0, 8, 16, DpmsProperty, _dpms);
                break;
            case (CrtcId, 0xcccccccc or 0) or (EncoderId, 0xe0e0e0e0 or 0):
                Put(data, 16, 0);
                break;
            default:
                return -(int)Errno.ENOENT;
        }
        return 0;
    }

    private static void WritePropertyList(Engine cpu, Span<byte> data, int ids, int values, int count, uint id, uint value)
    {
        if (U32(data, count) >= 1)
        {
            CopyId(cpu, U64(data, ids), 1, id);
            Span<byte> propertyValue = stackalloc byte[8];
            Put64(propertyValue, 0, value);
            CopyOut(cpu, U64(data, values), propertyValue);
        }
        Put(data, count, 1);
    }

    private static int Property(Engine cpu, Span<byte> data)
    {
        ReadOnlySpan<string> names;
        ReadOnlySpan<byte> name;
        uint flags = 1 << 3; // DRM_MODE_PROP_ENUM
        switch (U32(data, 16))
        {
            case PlaneTypeProperty:
                name = "type"u8;
                names = PlaneTypeNames;
                flags |= 1 << 2; // DRM_MODE_PROP_IMMUTABLE
                break;
            case DpmsProperty:
                name = "DPMS"u8;
                names = DpmsNames;
                break;
            default:
                return -(int)Errno.ENOENT;
        }

        if (U32(data, 56) >= names.Length)
        {
            Span<byte> values = stackalloc byte[names.Length * sizeof(ulong)];
            for (var i = 0; i < names.Length; i++) Put64(values, i * sizeof(ulong), (uint)i);
            CopyOut(cpu, U64(data, 0), values);
        }
        if (U32(data, 60) >= names.Length)
        {
            const int enumBytes = 40;
            Span<byte> enums = stackalloc byte[names.Length * enumBytes];
            enums.Clear();
            for (var i = 0; i < names.Length; i++)
            {
                Put64(enums, i * enumBytes, (uint)i);
                System.Text.Encoding.ASCII.GetBytes(names[i], enums[(i * enumBytes + 8)..]);
            }
            CopyOut(cpu, U64(data, 8), enums);
        }
        data.Slice(24, 32).Clear();
        name.CopyTo(data[24..]);
        Put(data, 20, flags);
        Put(data, 56, (uint)names.Length);
        Put(data, 60, (uint)names.Length);
        return 0;
    }

    private int SetDpms(Session session, ReadOnlySpan<byte> data)
    {
        if (_master != session) return -(int)Errno.EACCES;
        if (U32(data, 8) != DpmsProperty || U32(data, 12) != ConnectorId) return -(int)Errno.ENOENT;
        if (U64(data, 0) > 3) return -(int)Errno.EINVAL;
        _dpms = U32(data, 0);
        return 0;
    }

    private int DisableHardwareCursor(Session session, ReadOnlySpan<byte> data)
    {
        if (_master != session) return -(int)Errno.EACCES;
        if (U32(data, 4) != CrtcId) return -(int)Errno.ENOENT;
        // Only the primary plane exists. The legacy disable ioctl is idempotent.
        return U32(data, 0) == 1 && U32(data, 24) == 0 ? 0 : -(int)Errno.EOPNOTSUPP;
    }

    private static int ListNoLessees(Span<byte> data)
    {
        Put(data, 0, 0);
        return 0;
    }

    private int ExportPrime(Session session, FiberTask task, uint arg, Span<byte> data)
    {
        var flags = U32(data, 4);
        if ((flags & ~(PrimeCloseOnExec | PrimeReadWrite)) != 0) return -(int)Errno.EINVAL;
        if (!session.Buffers.TryGetValue(U32(data, 0), out var buffer)) return -(int)Errno.ENOENT;
        if (buffer.File == null && buffer.Gpu?.PrimeFormat is not > 0) return -(int)Errno.EOPNOTSUPP;

        var syscalls = task.Process.Syscalls;
        var inode = new PrimeInode(this, buffer);
        var dentry = new Dentry("dmabuf:podish", inode, null, SuperBlock);
        var file = new LinuxFile(dentry, (flags & PrimeReadWrite) == 0 ? FileFlags.O_RDONLY : FileFlags.O_RDWR,
            syscalls.AnonMount);
        var fd = syscalls.AllocFD(file, closeOnExec: (flags & PrimeCloseOnExec) != 0, task: task);
        if (fd < 0) return fd;
        Put(data, 8, (uint)fd);
        if (task.CPU.CopyToUser(arg, data)) return 0;
        syscalls.TryFreeFD(fd, task);
        return -(int)Errno.EFAULT;
    }

    private int AllocateGpuBuffer(LinuxFile file, FiberTask task, uint arg, Span<byte> data)
    {
        if (_allocateGpu == null || file.OpenedInode is not RenderNode) return -(int)Errno.EOPNOTSUPP;
        if (U32(data, 8) is not (Xrgb8888 or Argb8888)) return -(int)Errno.EINVAL;
        var buffer = _allocateGpu(U32(data, 0), U32(data, 4), U32(data, 8));
        if (buffer == null) return -(int)Errno.ENOMEM;

        try
        {
            var handle = RegisterGpuBuffer(file, buffer);
            if (handle == 0) return -(int)Errno.ENOSPC;
            Put(data, 12, handle);
            Put(data, 16, buffer.Pitch);
            if (task.CPU.CopyToUser(arg, data)) return 0;
            UnregisterGpuBuffer(file, handle);
            return -(int)Errno.EFAULT;
        }
        finally
        {
            buffer.Release();
        }
    }

    public static DrmScanoutBuffer? GetGpuPrimeBuffer(LinuxFile? file, uint width, uint height, uint pitch, uint format)
    {
        if (file?.OpenedInode is not PrimeInode { Buffer.Gpu: { } buffer } ||
            buffer.Width != width || buffer.Height != height || buffer.Pitch != pitch ||
            buffer.PrimeFormat != format || format == 0)
            return null;

        return buffer;
    }

    private int ImportPrime(Session session, FiberTask task, uint arg, Span<byte> data, bool gbm)
    {
        if (!gbm && U32(data, 4) != 0) return -(int)Errno.EINVAL;
        var source = task.Process.Syscalls.GetFD((int)U32(data, gbm ? 0 : 8), task);
        if (source == null) return -(int)Errno.EBADF;
        if (source.OpenedInode is not PrimeInode prime || prime.Card != this) return -(int)Errno.EINVAL;

        // Each GBM object owns its own handle, even when several BOs import the same fd.
        // The standard PRIME ioctl, in contrast, returns an existing GEM handle.
        if (gbm)
        {
            if (prime.Buffer.File == null) return -(int)Errno.EOPNOTSUPP;
            var width = U32(data, 4);
            var height = U32(data, 8);
            if (width == 0 || height == 0 || width > prime.Buffer.Width || height > prime.Buffer.Height ||
                U32(data, 12) != prime.Buffer.Pitch)
                return -(int)Errno.EINVAL;
            Put64(data, 20, prime.Buffer.Size);
        }
        else
        {
            foreach (var (existing, buffer) in session.Buffers)
            {
                if (!ReferenceEquals(buffer, prime.Buffer)) continue;
                Put(data, 0, existing);
                return task.CPU.CopyToUser(arg, data) ? 0 : -(int)Errno.EFAULT;
            }
        }

        if (session.Buffers.Count >= MaxObjects || (ulong)_nextId * BufferStride / LinuxConstants.PageSize > uint.MaxValue)
            return -(int)Errno.ENOSPC;
        var id = _nextId++;
        Put(data, gbm ? 16 : 0, id);
        if (!task.CPU.CopyToUser(arg, data)) return -(int)Errno.EFAULT;
        prime.Buffer.Retain();
        session.Buffers.Add(id, prime.Buffer);
        return 0;
    }

    private sealed class PrimeInode : Inode
    {
        internal PrimeInode(DrmCardInode card, Buffer buffer)
        {
            Card = card;
            Buffer = buffer;
            SuperBlock = card.SuperBlock;
            Ino = buffer.File?.OpenedInode?.Ino ?? (0x100000000UL + buffer.ObjectId);
            Type = InodeType.File;
            Mode = 0x180;
            Size = buffer.Size;
            AnonymousInodeLifecycle.Initialize(this, "DRM PRIME export");
        }

        internal DrmCardInode Card { get; }
        internal Buffer Buffer { get; }
        public override bool SupportsMmap => Buffer.File != null;

        public override void Open(LinuxFile file) => Buffer.Retain();

        public override void Release(LinuxFile file)
        {
            Buffer.Release();
            AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "DRM PRIME close");
        }

        public override int ResolveMmapBacking(LinuxFile file, ulong length, ref long offset, out LinuxFile backing)
        {
            backing = Buffer.File!;
            return offset >= 0 && (ulong)offset + length <= Buffer.Size ? 0 : -(int)Errno.EINVAL;
        }

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
        {
            if (request != 0x40086200) return -(int)Errno.ENOTTY; // DMA_BUF_IOCTL_SYNC
            if (Buffer.File == null) return -(int)Errno.EOPNOTSUPP;
            Span<byte> data = stackalloc byte[8];
            if (!task.CPU.CopyFromUser(arg, data)) return -(int)Errno.EFAULT;
            var flags = U64(data, 0);
            if ((flags & ~7ul) != 0 || (flags & 3) == 0) return -(int)Errno.EINVAL;
            if ((flags & 2) != 0 && (file.Flags & FileFlags.O_RDWR) == 0) return -(int)Errno.EACCES;
            return 0; // Shared CPU mappings are coherent in the emulator.
        }
    }

    public Inode CreateRenderNode() => new RenderNode(this);

    private sealed class RenderNode : Inode
    {
        private readonly DrmCardInode _card;
        internal DrmCardInode Card => _card;

        internal RenderNode(DrmCardInode card)
        {
            _card = card;
            SuperBlock = card.SuperBlock;
            Type = InodeType.CharDev;
            Ino = 0x22680;
            Mode = 0x1b6;
            Rdev = 0xe280;
            SetInitialLinkCount(1, "Virtual DRM render node registration");
        }

        public override void Open(LinuxFile file)
        {
            base.Open(file);
            file.PrivateData = new Session(_card._scheduler);
        }

        public override void Release(LinuxFile file)
        {
            var session = (Session)file.PrivateData!;
            foreach (var handle in session.Buffers.Keys.ToArray()) CloseBuffer(session, handle);
            base.Release(file);
        }

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg) => request switch
        {
            Version or VersionAArch64 or GetCap or PrimeImport or PrimeExport or GemClose or ImportGbmBo or
                CreateDumb or MapDumb or DestroyDumb or QueryGpuBuffers or CreateGpuBo
                => _card.Ioctl(file, task, request, arg),
            _ => -(int)Errno.EACCES
        };

        public override bool SupportsMmap => true;

        public override int ResolveMmapBacking(LinuxFile file, ulong length, ref long offset, out LinuxFile backing) =>
            _card.ResolveMmapBacking(file, length, ref offset, out backing);
    }
}
