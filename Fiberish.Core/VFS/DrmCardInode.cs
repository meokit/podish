using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>A virtual DRM primary node with one legacy KMS output and CPU or GPU buffers.</summary>
public sealed partial class DrmCardInode : Inode, ITaskWaitSource, IPollNotificationSource
{
    private const uint CrtcId = 1, ConnectorId = 2, EncoderId = 3;
    public const int ModeWidth = 640, ModeHeight = 480;
    private const int EventSize = 32, MaxEvents = 256;
    private const short PollIn = 0x0001;
    private const int MaxObjects = 64;
    private const uint BufferStride = 64 * 1024 * 1024;
    private const uint Xrgb8888 = 0x34325258;
    private const uint Argb8888 = 0x34325241;

    // The fixed-width mode-setting structs can still differ in trailing
    // padding, and the legacy version query uses size_t and pointers.
    private const uint Version = 0xC0246400, VersionAArch64 = 0xC0406400, GemClose = 0x40086409, GetCap = 0xC010640C;
    private const uint AuthMagic = 0x40046411, SetMaster = 0x641E, DropMaster = 0x641F;
    private const uint GetResources = 0xC04064A0, GetCrtc = 0xC06864A1, SetCrtc = 0xC06864A2;
    private const uint GetEncoder = 0xC01464A6, GetConnector = 0xC05064A7;
    private const uint AddFb = 0xC01C64AE, RemoveFb = 0xC00464AF, PageFlip = 0xC01864B0;
    private const uint CreateDumb = 0xC02064B2, MapDumb = 0xC01064B3, DestroyDumb = 0xC00464B4;
    private const uint AddFb2 = 0xC06464B8, AddFb2AArch64 = 0xC06864B8;
    private const uint GetProperties = 0xC01C64B9, GetPropertiesAArch64 = 0xC02064B9;

    // Podish-only ioctls used by the guest GBM library. Standard libdrm is unmodified.
    private const uint QueryGpuBuffers = 0x800464F0, LockGpuBuffer = 0x400864F1;
    private const uint CreateGpuBo = 0xc01464f3;

    private readonly KernelScheduler _scheduler;
    private readonly bool _gpuBuffers;
    private readonly FramebufferDevice? _cpuOutput;
    private readonly Func<uint, uint, uint, DrmScanoutBuffer?>? _allocateGpu;
    private readonly uint _width;
    private readonly uint _height;
    private readonly byte[] _mode;
    private readonly VirtualDrmScanout? _compositorOutput;
    private Session? _compositorSession;
    private readonly uint _compositorFirstFramebuffer;
    private Session? _master;
    private Session? _scanoutOwner;
    private uint _scanout;
    private DrmScanoutBuffer? _activeGpu;
    private PendingFlip? _pending;
    private uint _nextId = DpmsProperty + 1;

    public DrmCardInode(SuperBlock superBlock, KernelScheduler scheduler, bool gpuBuffers,
        FramebufferDevice? cpuOutput = null, Func<uint, uint, uint, DrmScanoutBuffer?>? allocateGpu = null,
        VirtualDrmScanout? compositorOutput = null)
    {
        _scheduler = scheduler;
        _gpuBuffers = gpuBuffers;
        _cpuOutput = cpuOutput;
        _allocateGpu = allocateGpu;
        _width = (uint)(compositorOutput?.Width ?? ModeWidth);
        _height = (uint)(compositorOutput?.Height ?? ModeHeight);
        _mode = CreateMode((int)_width, (int)_height);
        SuperBlock = superBlock;
        Type = InodeType.CharDev;
        Ino = 0x22600;
        Mode = 0x1b6;
        Rdev = 0xe200; // DRM major 226, card0 minor 0.
        SetInitialLinkCount(1, "Virtual DRM registration");

        if (compositorOutput != null)
        {
            _compositorOutput = compositorOutput;
            _master = _compositorSession = new Session(scheduler);
            _compositorFirstFramebuffer = _nextId;
            var pitch = (uint)compositorOutput.Pitch;
            for (int i = 0; i < 2; i++)
            {
                var backing = new Buffer(null, _width, _height, pitch, pitch * _height,
                    compositorOutput.GpuBuffers?[i]);
                backing.Retain();
                _compositorSession.Framebuffers.Add(_nextId++, new Framebuffer(backing, _width, _height));
            }
            compositorOutput.Attach(this);
        }
    }

    internal void SubmitCompositorFlip(int bufferIndex)
    {
        _scheduler.ScheduleFromAnyThread(() =>
        {
            if (_compositorSession is { } session)
            {
                uint framebuffer = _compositorFirstFramebuffer + (uint)bufferIndex;
                var gpu = session.Framebuffers[framebuffer].Backing.Gpu;
                gpu?.Pin();
                ScheduleFlip(new PendingFlip(session, framebuffer, false, 0, gpu));
            }
        });
    }

    internal void ReleaseCompositorScanout()
    {
        if (_compositorSession is not { } session)
            return;

        Deactivate(session);
        foreach (var framebuffer in session.Framebuffers.Values)
            framebuffer.Backing.Release();
        session.Framebuffers.Clear();
        _compositorSession = null;
        _master = null;
    }

    private sealed record Framebuffer(Buffer Backing, uint Width, uint Height);

    private sealed record Buffer(LinuxFile? File, uint Width, uint Height, uint Pitch, uint Size,
        DrmScanoutBuffer? Gpu = null)
    {
        internal uint ObjectId { get; init; }

        internal void Retain()
        {
            File?.Get();
            Gpu?.Retain();
        }

        internal void Release()
        {
            File?.Close();
            Gpu?.Release();
        }
    }

    private sealed record PendingFlip(Session Owner, uint Fb, bool Event, ulong UserData, DrmScanoutBuffer? Gpu);

    private sealed class Session(KernelScheduler scheduler)
    {
        internal readonly Dictionary<uint, Buffer> Buffers = [];
        internal readonly Dictionary<uint, Framebuffer> Framebuffers = [];
        internal readonly Queue<byte[]> Events = [];
        internal readonly AsyncWaitQueue Readable = new(scheduler);
        internal Fiberish.Core.Timer? Timer;
        internal uint Magic;
        internal bool UniversalPlanes;
    }

    public override bool SupportsMmap => true;

    public override void Open(LinuxFile file)
    {
        base.Open(file);
        var session = new Session(_scheduler);
        file.PrivateData = session;
        if ((file.Flags & (FileFlags)0x200000) == 0) // O_PATH does not acquire DRM master.
            _master ??= session;
    }

    public override void Release(LinuxFile file)
    {
        var session = (Session)file.PrivateData!;
        Deactivate(session);
        _pendingMagic.Remove(session.Magic);
        if (_master == session) _master = null;
        foreach (var fb in session.Framebuffers.Values) fb.Backing.Release();
        foreach (var handle in session.Buffers.Keys.ToArray()) CloseBuffer(session, handle);
        session.Framebuffers.Clear();
        base.Release(file);
    }

    public uint RegisterGpuBuffer(LinuxFile file, DrmScanoutBuffer buffer)
    {
        if (!_gpuBuffers || (file.OpenedInode != this &&
                             (file.OpenedInode is not RenderNode render || render.Card != this)) ||
            file.PrivateData is not Session session ||
            (file.Flags & FileFlags.O_RDWR) == 0 || session.Buffers.Count >= MaxObjects)
            return 0;
        var id = _nextId++;
        var backing = new Buffer(null, buffer.Width, buffer.Height, buffer.Pitch, buffer.Pitch * buffer.Height, buffer)
            { ObjectId = id };
        backing.Retain();
        session.Buffers.Add(id, backing);
        return id;
    }

    public void UnregisterGpuBuffer(LinuxFile file, uint id)
    {
        if (file.PrivateData is Session session && session.Buffers.TryGetValue(id, out var buffer) &&
            buffer.Gpu != null)
            CloseBuffer(session, id);
    }

    public override int ResolveMmapBacking(LinuxFile file, ulong length, ref long offset, out LinuxFile backing)
    {
        backing = file;
        if (offset < BufferStride || (ulong)offset / BufferStride > uint.MaxValue) return -(int)Errno.EINVAL;
        var handle = (uint)(offset / BufferStride);
        var position = (uint)(offset % BufferStride);
        var session = (Session)file.PrivateData!;
        if (!session.Buffers.TryGetValue(handle, out var buffer) || buffer.File == null || (ulong)position + length > buffer.Size)
            return -(int)Errno.EINVAL;
        backing = buffer.File;
        offset = position;
        return 0;
    }

    public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
    {
        var session = (Session)file.PrivateData!;
        if (request == QueryGpuBuffers)
        {
            Span<byte> flags = stackalloc byte[4];
            Put(flags, 0, file.OpenedInode is RenderNode ? (_allocateGpu != null ? 2u : 0) : (_gpuBuffers ? 1u : 0));
            return arg != 0 && (ulong)arg + 4 <= 1UL << 32 && task.CPU.CopyToUser(arg, flags)
                ? 0 : -(int)Errno.EFAULT;
        }
        if (request == SetMaster)
        {
            if (_master != null && _master != session) return -(int)Errno.EBUSY;
            _master = session;
            return 0;
        }
        if (request == DropMaster)
        {
            if (_master != session) return -(int)Errno.EACCES;
            _master = null;
            return 0;
        }

        if (request == CreateLease) return -(int)Errno.EOPNOTSUPP;

        var cpu = task.CPU;
        var operation = cpu.IsAArch64 ? request switch
        {
            VersionAArch64 => Version,
            AddFb2AArch64 => AddFb2,
            GetPropertiesAArch64 => GetProperties,
            GetPlaneResourcesAArch64 => GetPlaneResources,
            Version or AddFb2 or GetProperties or GetPlaneResources => 0,
            _ => request
        } : request;

        var size = operation switch
        {
            Version or GemClose or GetCap or AuthMagic or GetResources or GetCrtc or SetCrtc or GetEncoder or
                GetConnector or AddFb or AddFb2 or RemoveFb or PageFlip or CreateDumb or MapDumb or DestroyDumb or
                GetProperties or LockGpuBuffer or GetMagic or SetClientCap or PrimeExport or PrimeImport or
                GetPlaneResources or GetPlane or GetProperty or SetConnectorProperty or SetCursor or
                ListLessees or ImportGbmBo or CreateGpuBo => (int)((request >> 16) & 0x3fff),
            _ => 0
        };
        if (size == 0) return -(int)Errno.ENOTTY;
        Span<byte> data = stackalloc byte[size];
        if (arg == 0 || (ulong)arg + (uint)size > 1UL << 32 || !cpu.CopyFromUser(arg, data))
            return -(int)Errno.EFAULT;

        try
        {
            var rc = operation switch
            {
                Version => GetVersion(cpu, data),
                GetCap => GetCapability(data),
                GetMagic => GetAuthenticationMagic(session, data),
                AuthMagic => Authenticate(session, data),
                SetClientCap => SetClientCapability(session, data),
                GetResources => Resources(session, cpu, data),
                GetCrtc => Crtc(data),
                SetCrtc => SetMode(session, cpu, data),
                GetEncoder => Encoder(data),
                GetConnector => Connector(cpu, data),
                GetProperties => ObjectProperties(cpu, data),
                GetPlaneResources => PlaneResources(session, cpu, data),
                GetPlane => Plane(session, cpu, data),
                GetProperty => Property(cpu, data),
                SetConnectorProperty => SetDpms(session, data),
                SetCursor => DisableHardwareCursor(session, data),
                ListLessees => ListNoLessees(data),
                PrimeImport or ImportGbmBo => ImportPrime(session, task, arg, data, request == ImportGbmBo),
                PrimeExport => ExportPrime(session, task, arg, data),
                CreateGpuBo => AllocateGpuBuffer(file, task, arg, data),
                CreateDumb => AllocateBuffer(session, task, arg, data),
                MapDumb => GetBufferOffset(session, data),
                DestroyDumb or GemClose => CloseBuffer(session, U32(data, 0)),
                AddFb or AddFb2 => AddFramebuffer(session, cpu, arg, data, operation == AddFb2),
                RemoveFb => RemoveFramebuffer(session, U32(data, 0)),
                PageFlip => QueueFlip(session, data),
                LockGpuBuffer => SetGuestBufferLock(session, U32(data, 0), U32(data, 4)),
                _ => -(int)Errno.ENOTTY
            };
            // Mutations returning identifiers validate their writeback before publishing the object.
            if (rc != 0 || operation is CreateDumb or AddFb or AddFb2 or GemClose or DestroyDumb or RemoveFb
                or SetCrtc or PageFlip or LockGpuBuffer or SetClientCap or AuthMagic or SetConnectorProperty
                or SetCursor or PrimeExport or PrimeImport or ImportGbmBo or CreateGpuBo)
                return rc;
            return cpu.CopyToUser(arg, data) ? 0 : -(int)Errno.EFAULT;
        }
        catch (UserMemoryFault)
        {
            return -(int)Errno.EFAULT;
        }
    }

    private static int GetVersion(Engine cpu, Span<byte> data)
    {
        Put(data, 0, 1);
        Put(data, 4, 0);
        Put(data, 8, 0);
        var wordSize = cpu.IsAArch64 ? 8 : 4;
        var offset = cpu.IsAArch64 ? 16 : 12;
        WriteVersionString(cpu, data[offset..], wordSize, "podish"u8);
        WriteVersionString(cpu, data[(offset + 2 * wordSize)..], wordSize, "20261118"u8);
        WriteVersionString(cpu, data[(offset + 4 * wordSize)..], wordSize, "Podish virtual KMS"u8);
        return 0;
    }

    private static void WriteVersionString(Engine cpu, Span<byte> data, int wordSize, ReadOnlySpan<byte> value)
    {
        var length = wordSize == 8 ? U64(data, 0) : U32(data, 0);
        var address = wordSize == 8 ? U64(data, wordSize) : U32(data, wordSize);
        var bytes = (int)Math.Min((ulong)value.Length, length);
        if (bytes > 0) CopyOut(cpu, address, value[..bytes]);
        if (wordSize == 8)
            Put64(data, 0, (ulong)value.Length);
        else
            Put(data, 0, (uint)value.Length);
    }

    private static int GetCapability(Span<byte> data)
    {
        var value = U64(data, 0) switch
        {
            0x1 or 0x6 or 0x10 or 0x12 => 1, // dumb BO, monotonic timestamps, modifiers, CRTC in event
            0x3 => 24,                      // preferred dumb-buffer depth
            0x5 => 3, // PRIME import and export for linear BOs
            0x2 or 0x4 or 0x7 or 0x13 or 0x14 => 0,
            _ => -1
        };
        if (value < 0) return -(int)Errno.EINVAL;
        Put64(data, 8, (ulong)value);
        return 0;
    }

    private static int Resources(Session session, Engine cpu, Span<byte> data)
    {
        Span<uint> ids = stackalloc uint[session.Framebuffers.Count];
        var i = 0;
        foreach (var id in session.Framebuffers.Keys) ids[i++] = id;
        if (ids.Length > 0 && U32(data, 32) >= ids.Length)
        {
            Span<byte> output = stackalloc byte[ids.Length * sizeof(uint)];
            for (i = 0; i < ids.Length; i++) Put(output, i * sizeof(uint), ids[i]);
            CopyOut(cpu, U64(data, 0), output);
        }
        CopyId(cpu, U64(data, 8), U32(data, 36), CrtcId);
        CopyId(cpu, U64(data, 16), U32(data, 40), ConnectorId);
        CopyId(cpu, U64(data, 24), U32(data, 44), EncoderId);
        Put(data, 32, (uint)ids.Length);
        for (var offset = 36; offset <= 48; offset += sizeof(uint)) Put(data, offset, 1);
        Put(data, 56, 1);
        Put(data, 52, FramebufferDevice.DimensionLimit);
        Put(data, 60, FramebufferDevice.DimensionLimit);
        return 0;
    }

    private int Connector(Engine cpu, Span<byte> data)
    {
        if (U32(data, 48) != ConnectorId) return -(int)Errno.ENOENT;
        if (U32(data, 32) > 0) CopyOut(cpu, U64(data, 8), _mode);
        CopyId(cpu, U64(data, 0), U32(data, 40), EncoderId);
        Put(data, 32, 1);
        WritePropertyList(cpu, data, 16, 24, 36, DpmsProperty, _dpms);
        Put(data, 40, 1);
        Put(data, 44, _scanout != 0 ? EncoderId : 0);
        Put(data, 52, 15); // DRM_MODE_CONNECTOR_VIRTUAL
        Put(data, 56, 1);
        Put(data, 60, 1); // DRM_MODE_CONNECTED
        data[64..].Clear(); // No physical dimensions or subpixel order.
        return 0;
    }

    private int Encoder(Span<byte> data)
    {
        if (U32(data, 0) != EncoderId) return -(int)Errno.ENOENT;
        Put(data, 4, 5); // DRM_MODE_ENCODER_VIRTUAL
        Put(data, 8, _scanout != 0 ? CrtcId : 0);
        Put(data, 12, 1); // Can drive the first CRTC.
        Put(data, 16, 0);
        return 0;
    }

    private int Crtc(Span<byte> data)
    {
        if (U32(data, 12) != CrtcId) return -(int)Errno.ENOENT;
        data[16..].Clear();
        Put(data, 16, _scanout);
        if (_scanout != 0)
        {
            Put(data, 32, 1);
            _mode.CopyTo(data[36..]);
        }
        return 0;
    }

    private int AllocateBuffer(Session session, FiberTask task, uint arg, Span<byte> data)
    {
        var height = U32(data, 0);
        var width = U32(data, 4);
        if (width is 0 or > FramebufferDevice.DimensionLimit || height is 0 or > FramebufferDevice.DimensionLimit ||
            U32(data, 8) != 32 || U32(data, 12) != 0)
            return -(int)Errno.EINVAL;
        var pitch = (width * 4 + 63) & ~63u;
        var length = (pitch * height + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        var allocated = session.Buffers.Values.Concat(session.Framebuffers.Values.Select(fb => fb.Backing))
            .Where(b => b.File != null).DistinctBy(b => b.File).Sum(b => (long)b.Size);
        if (session.Buffers.Count >= MaxObjects || allocated + length > BufferStride)
            return -(int)Errno.ENOMEM;
        if ((ulong)_nextId * BufferStride / LinuxConstants.PageSize > uint.MaxValue)
            return -(int)Errno.ENOSPC;
        var id = _nextId++;
        Put(data, 16, id);
        Put(data, 20, pitch);
        Put64(data, 24, length);
        if (!task.CPU.CopyToUser(arg, data)) return -(int)Errno.EFAULT;
        var backing = task.CPU.MemoryContext.CreateDrmBufferFile(length, task.Process.EUID,
            task.Process.EGID, task.Process.Syscalls.AnonMount);
        session.Buffers.Add(id, new Buffer(backing, width, height, pitch, length));
        return 0;
    }

    private static int GetBufferOffset(Session session, Span<byte> data)
    {
        var id = U32(data, 0);
        if (!session.Buffers.TryGetValue(id, out var buffer)) return -(int)Errno.ENOENT;
        if (buffer.File == null) return -(int)Errno.ENODEV;
        Put64(data, 8, (ulong)id * BufferStride);
        return 0;
    }

    private static int CloseBuffer(Session session, uint id)
    {
        if (!session.Buffers.Remove(id, out var buffer)) return -(int)Errno.ENOENT;
        if (buffer.Gpu != null) buffer.Gpu.GuestLocked = false;
        buffer.Release();
        return 0;
    }

    private static int SetGuestBufferLock(Session session, uint id, uint value)
    {
        if (!session.Buffers.TryGetValue(id, out var buffer) || buffer.Gpu is not { } gpu)
            return -(int)Errno.ENOENT;
        if (value > 1 || gpu.GuestLocked == (value == 1) || (value == 1 && !gpu.Ready))
            return -(int)Errno.EINVAL;
        gpu.GuestLocked = value == 1;
        return 0;
    }

    private int AddFramebuffer(Session session, Engine cpu, uint arg, Span<byte> data, bool extended)
    {
        var handle = U32(data, extended ? 20 : 24);
        if (!session.Buffers.TryGetValue(handle, out var buffer)) return -(int)Errno.ENOENT;
        if (session.Framebuffers.Count >= MaxObjects) return -(int)Errno.ENOSPC;
        var width = U32(data, 4);
        var height = U32(data, 8);
        if (width == 0 || height == 0 || width > buffer.Width || height > buffer.Height ||
            (buffer.Gpu != null && (width != buffer.Width || height != buffer.Height)))
            return -(int)Errno.EINVAL;
        if (extended)
        {
            // One linear plane. The format and plane count are part of the framebuffer, not the BO handle.
            if (U32(data, 12) is not (Xrgb8888 or Argb8888) || (U32(data, 16) & ~2u) != 0 ||
                U32(data, 36) != buffer.Pitch || data[24..36].ContainsAnyExcept((byte)0) ||
                data[40..68].ContainsAnyExcept((byte)0) ||
                data[(cpu.IsAArch64 ? 72 : 68)..].ContainsAnyExcept((byte)0) ||
                (buffer.Gpu is { PrimeFormat: not 0 } gpu && gpu.PrimeFormat != U32(data, 12)))
                return -(int)Errno.EINVAL;
        }
        else if (U32(data, 12) != buffer.Pitch || U32(data, 16) != 32 || U32(data, 20) is not (24 or 32))
            return -(int)Errno.EINVAL;
        var id = _nextId++;
        Put(data, 0, id);
        if (!cpu.CopyToUser(arg, data)) return -(int)Errno.EFAULT;
        buffer.Retain();
        session.Framebuffers.Add(id, new Framebuffer(buffer, width, height));
        return 0;
    }

    private int RemoveFramebuffer(Session session, uint id)
    {
        if (!session.Framebuffers.TryGetValue(id, out var fb)) return -(int)Errno.ENOENT;
        if (_pending is { } pending && (pending.Fb == id || _scanout == id)) return -(int)Errno.EBUSY;
        session.Framebuffers.Remove(id);
        if (id == _scanout) Deactivate(session);
        fb.Backing.Release();
        return 0;
    }

    private int SetMode(Session session, Engine cpu, ReadOnlySpan<byte> data)
    {
        if (_master != session) return -(int)Errno.EACCES;
        if (U32(data, 12) != CrtcId) return -(int)Errno.ENOENT;
        var fb = U32(data, 16);
        if (U32(data, 32) == 0 || fb == 0)
        {
            if (_pending is { } pending)
            {
                pending.Owner.Timer?.Cancel();
                CompleteFlip();
            }
            Activate(null, 0, null);
            return 0;
        }
        if (_pending != null) return -(int)Errno.EBUSY;
        if (!session.Framebuffers.TryGetValue(fb, out var buffer)) return -(int)Errno.ENOENT;
        if (U32(data, 8) != 1 || U32(data, 20) != 0 || U32(data, 24) != 0 ||
            buffer.Width != _width || buffer.Height != _height ||
            !data.Slice(36, 32).SequenceEqual(_mode.AsSpan(0, 32)))
            return -(int)Errno.EINVAL;
        Span<byte> connector = stackalloc byte[sizeof(uint)];
        var pointer = U64(data, 0);
        if (pointer == 0 || pointer > uint.MaxValue - 3 || !cpu.CopyFromUser((uint)pointer, connector))
            return -(int)Errno.EFAULT;
        if (U32(connector, 0) != ConnectorId) return -(int)Errno.ENOENT;
        if (buffer.Backing.Gpu is { Ready: false } || !PresentBuffer(buffer)) return -(int)Errno.EIO;
        Activate(session, fb, buffer.Backing.Gpu);
        return 0;
    }

    private bool PresentBuffer(Framebuffer framebuffer)
    {
        var buffer = framebuffer.Backing;
        if (buffer.Gpu is { } gpu) return gpu.Present();
        if (_cpuOutput != null && buffer.File is { } file)
            _cpuOutput.TryPresentXrgb8888(file, (int)framebuffer.Width, (int)framebuffer.Height, (int)buffer.Pitch);
        return true;
    }

    private void Activate(Session? owner, uint id, DrmScanoutBuffer? gpu)
    {
        gpu?.Pin();
        _activeGpu?.Unpin();
        _activeGpu = gpu;
        _scanoutOwner = owner;
        _scanout = id;
    }

    private int QueueFlip(Session session, ReadOnlySpan<byte> data)
    {
        if (_master != session) return -(int)Errno.EACCES;
        if (U32(data, 0) != CrtcId) return -(int)Errno.ENOENT;
        var flags = U32(data, 8);
        if (U32(data, 12) != 0 || (flags & ~1u) != 0 || _scanout == 0) return -(int)Errno.EINVAL;
        if (_pending != null) return -(int)Errno.EBUSY;
        var fb = U32(data, 4);
        if (!session.Framebuffers.TryGetValue(fb, out var buffer)) return -(int)Errno.ENOENT;
        if (buffer.Width != _width || buffer.Height != _height) return -(int)Errno.EINVAL;
        if ((flags & 1) != 0 && session.Events.Count >= MaxEvents) return -(int)Errno.EBUSY;

        var gpu = buffer.Backing.Gpu;
        if (gpu is { Ready: false }) return -(int)Errno.EINVAL;
        gpu?.Pin();
        ScheduleFlip(new PendingFlip(session, fb, (flags & 1) != 0, U64(data, 16), gpu));
        return 0;
    }

    private void ScheduleFlip(PendingFlip flip)
    {
        _pending = flip;
        var now = MonotonicClock.Milliseconds;
        var next = ((now * 60 / 1000 + 1) * 1000 + 59) / 60;
        flip.Owner.Timer = _scheduler.ScheduleTimer(next - now, CompleteFlip);
    }

    private void CompleteFlip()
    {
        if (_pending is not { } flip) return;
        var session = flip.Owner;
        session.Timer = null;
        _pending = null;
        try
        {
            // The built-in compositor owns its EGL context on the display thread.
            if (session == _compositorSession || PresentBuffer(session.Framebuffers[flip.Fb]))
                Activate(session, flip.Fb, flip.Gpu);
        }
        finally
        {
            flip.Gpu?.Unpin();
        }
        var now = MonotonicClock.Milliseconds;
        if (session == _compositorSession)
            _compositorOutput!.CompleteFlip((int)(flip.Fb - _compositorFirstFramebuffer), unchecked((uint)now));
        if (!flip.Event) return;

        var data = new byte[EventSize];
        Put(data, 0, 2); // DRM_EVENT_FLIP_COMPLETE
        Put(data, 4, EventSize);
        Put64(data, 8, flip.UserData);
        Put(data, 16, (uint)(now / 1000));
        Put(data, 20, (uint)(now % 1000) * 1000);
        Put(data, 24, (uint)(now * 60 / 1000));
        Put(data, 28, CrtcId);
        session.Events.Enqueue(data);
        session.Readable.Set();
    }

    private void Deactivate(Session session)
    {
        if (_pending is { } flip && flip.Owner == session)
        {
            flip.Gpu?.Unpin();
            session.Timer?.Cancel();
            session.Timer = null;
            _pending = null;
        }
        if (_scanoutOwner == session) Activate(null, 0, null);
    }

    public override short Poll(LinuxFile file, short events) =>
        ((Session)file.PrivateData!).Events.Count > 0 ? (short)(events & PollIn) : (short)0;

    public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events) =>
        RegisterWaitHandle(file, task, callback, events) != null;

    public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        var session = (Session)file.PrivateData!;
        var ready = session.Readable;
        return QueueReadinessRegistration.RegisterHandle(callback, task, events,
            new QueueReadinessWatch(PollIn, () => session.Events.Count > 0, ready, ready.Reset));
    }

    IDisposable IPollNotificationSource.SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher,
        Func<short, bool> notify, short interests, bool exclusive) =>
        ((Session)file.PrivateData!).Readable.SubscribePoll(dispatcher, notify, interests, exclusive);

    public override async ValueTask<int> ReadV(Engine cpu, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        var session = (Session)file.PrivateData!;
        var capacity = iovs.Sum(iov => (long)iov.Len);
        if (capacity == 0) return 0;
        if (capacity < EventSize) return -(int)Errno.EINVAL;
        while (session.Events.Count == 0)
        {
            if (task == null || (file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 8) != 0)
                return -(int)Errno.EAGAIN;
            var ready = session.Readable;
            if (await ready.WaitInterruptiblyAsync(task) == AwaitResult.Interrupted) return -(int)Errno.EINTR;
        }

        return ReadEvents(session, cpu, iovs, capacity);
    }

    private static int ReadEvents(Session session, Engine cpu, ArraySegment<Iovec> iovs, long capacity)
    {
        int total = 0, i = 0;
        uint offset = 0;
        while (capacity - total >= EventSize && session.Events.TryPeek(out var data))
        {
            for (var copied = 0; copied < EventSize;)
            {
                var item = iovs[i];
                if (offset == item.Len) { i++; offset = 0; continue; }
                var size = (int)Math.Min(item.Len - offset, (uint)(EventSize - copied));
                var ptr = item.BaseAddr + offset;
                if (ptr == 0 || ptr < item.BaseAddr || !cpu.CopyToUser(ptr, data.AsSpan(copied, size)))
                    return total > 0 ? total : -(int)Errno.EFAULT;
                copied += size;
                offset += (uint)size;
            }
            session.Events.Dequeue();
            total += EventSize;
        }
        if (session.Events.Count == 0) session.Readable.Reset();
        return total;
    }

    private static byte[] CreateMode(int width, int height)
    {
        var mode = new byte[68];
        bool standardVga = width == ModeWidth && height == ModeHeight;
        int horizontalTotal = width + 160;
        int verticalTotal = height + 45;
        Put(mode, 0, standardVga ? 25175u : (uint)((horizontalTotal * verticalTotal * 60 + 500) / 1000));
        ushort[] timings =
        [
            (ushort)width, (ushort)(width + 16), (ushort)(width + 112), (ushort)horizontalTotal, 0,
            (ushort)height, (ushort)(height + 10), (ushort)(height + 12), (ushort)verticalTotal, 0
        ];
        for (var i = 0; i < timings.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(4 + i * 2), timings[i]);
        Put(mode, 24, 60);
        Put(mode, 28, (1u << 1) | (1u << 3)); // Negative HSync and VSync.
        Put(mode, 32, (1u << 3) | (1u << 6)); // Preferred, generated by the driver.
        System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{width}x{height}"), mode.AsSpan(36));
        return mode;
    }

    private static void CopyId(Engine cpu, ulong address, uint count, uint id)
    {
        if (count == 0) return;
        Span<byte> data = stackalloc byte[4];
        Put(data, 0, id);
        CopyOut(cpu, address, data);
    }

    private static void CopyOut(Engine cpu, ulong address, ReadOnlySpan<byte> data)
    {
        if (address == 0 || address > (1UL << 32) - (uint)data.Length || !cpu.CopyToUser((uint)address, data))
            throw new UserMemoryFault();
    }

    private sealed class UserMemoryFault : Exception;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    private static void Put(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    private static void Put64(Span<byte> data, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(data[offset..], value);
}
