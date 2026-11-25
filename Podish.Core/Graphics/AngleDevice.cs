using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

/// <summary>The Podish IA-32 GLES ioctl device. All native handles stay on the host.</summary>
internal sealed unsafe partial class AngleDevice : IDisposable
{
    internal const string Name = "podish-gles";
    private const uint RequestV1 = 0xC0285047, RequestV2 = 0xC0305047;
    private const uint Abi = 2;
    private const int MaxObjects = 64;
    private const int MaxSurfacePixels = 2 * FramebufferDevice.DimensionLimit * FramebufferDevice.DimensionLimit;
    private const uint Pbuffer = 0, Fbdev = 1, Wayland = 2, FbdevPbuffer = 3, Gbm = 4;

    private enum EglOperation : uint
    {
        Initialize = 1, CreateContext, DestroyContext, CreateSurface, DestroySurface,
        MakeCurrent, Present, ResizeSurface, FramebufferSize, ReleaseThread, SwapBuffers, CreateGbmSurface,
        ImportImage, DestroyImage, ImageRenderbuffer, ImageTexture, PresentWaylandGpu
    }

    private readonly AngleLibrary _angle;
    private readonly FramebufferDevice? _fb;
    private readonly KmsEglOutput? _kms;
    private nint Config => _kms?.Config ?? _angle.Config;
    internal bool HasKmsOutput { get; }
    private readonly HashSet<Session> _sessions = [];
    private Session? _hostSession;
    private Binding _hostBinding;
    private uint _nextSync = 1;
    private bool _disposed;

    internal AngleDevice(string directory, FramebufferDevice? framebuffer, KmsEglWindow? kmsWindow, ILogger logger,
        KmsEglOutput? compositorTextures = null)
    {
        _angle = AngleLibrary.Acquire(directory, kmsWindow);
        _fb = framebuffer;
        try
        {
            HasKmsOutput = kmsWindow != null;
            _kms = HasKmsOutput ? new KmsEglOutput(_angle, kmsWindow, logger) : compositorTextures;
        }
        catch
        {
            _angle.Release();
            throw;
        }
    }

    internal Inode CreateInode(SuperBlock superBlock) => new DeviceInode(superBlock, this);

    private sealed class DeviceInode : Inode
    {
        private readonly AngleDevice _device;

        internal DeviceInode(SuperBlock superBlock, AngleDevice device)
        {
            _device = device;
            SuperBlock = superBlock;
            Type = InodeType.CharDev;
            Ino = 0x5047;
            Mode = 0x1b6; // 0666
            Rdev = 0xf000; // Local virtual device major 240, minor 0.
            SetInitialLinkCount(1, "GLES device registration");
        }

        public override void Open(LinuxFile file)
        {
            base.Open(file);
            var session = new Session();
            file.PrivateData = session;
            _device._sessions.Add(session);
        }

        public override void Release(LinuxFile file)
        {
            if (file.PrivateData is Session session)
                _device.Close(session);
            base.Release(file);
        }

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg) =>
            request is RequestV1 or RequestV2 && file.PrivateData is Session session
                ? _device.Invoke(session, task, arg, request == RequestV2)
                : -(int)Errno.ENOTTY;
    }

    private sealed class Session
    {
        internal readonly Dictionary<uint, Context> Contexts = [];
        internal readonly Dictionary<uint, Surface> Surfaces = [];
        internal readonly Dictionary<FiberTask, Binding> Bindings = [];
        internal readonly Dictionary<uint, KmsEglOutput.TextureBo> Images = [];
        internal Process? Owner;
        internal uint NextId = 1;
    }

    private sealed class ShareGroup
    {
        internal readonly Dictionary<uint, nint> Syncs = [];
        internal readonly Dictionary<uint, BufferMapping> BufferMappings = [];
    }

    private sealed class Context(nint handle, uint version, ShareGroup shareGroup)
    {
        internal readonly nint Handle = handle;
        internal readonly uint Version = version;
        internal readonly ShareGroup Shared = shareGroup;
        internal readonly Dictionary<uint, Attribute> Attributes = [];
        internal uint Error;
        internal bool Deleted;
        internal ManagedFramebufferState? Managed;
    }

    private sealed class Surface(nint handle, int width, int height, uint kind)
    {
        internal nint Handle = handle;
        internal readonly uint Kind = kind;
        internal int Width = width;
        internal int Height = height;
        internal bool Deleted;
        internal GbmBuffers? Buffers;
    }

    private readonly record struct Binding(uint Context, uint Draw, uint Read);

    private int Invoke(Session session, FiberTask task, uint address, bool extended)
    {
        if (_disposed) return -(int)Errno.ENODEV;
        // Forked guests reopen the device and never reuse the parent's guest addresses.
        session.Owner ??= task.Process;
        if (!ReferenceEquals(session.Owner, task.Process)) return -(int)Errno.EPERM;
        var guest = new GuestMemory(task);
        Span<uint> wire = stackalloc uint[1 + 11];
        try
        {
            wire.Clear();
            guest.Read(address, MemoryMarshal.AsBytes(wire[..(extended ? 12 : 10)]));
            var op = wire[0];
            var args = wire[1..];
            int value;
            using (_angle.EnterDisplayLock())
            {
                if (op >= GlFirst && op - GlFirst < GlNames.Length)
                {
                    if (!session.Bindings.TryGetValue(task, out var current) || !Bind(session, current))
                        return -(int)Errno.ENXIO;
                    var context = session.Contexts[current.Context];
                    try
                    {
                        if (op == GlGetError && context.Error != 0)
                        {
                            value = (int)context.Error;
                            context.Error = 0;
                        }
                        else if (op >= GlEs3First && (!extended || context.Version < 3))
                            value = UnsupportedGl(context, op);
                        else
                            value = context.Managed != null && DispatchManagedGl(context, op, args, guest, out var managed)
                                ? managed : DispatchGl(context, op, args, guest);
                    }
                    catch (GuestTransferException ex) when (ex.Errno != Errno.EFAULT)
                    {
                        Flag(context, ex.Errno == Errno.E2BIG ? 0x0505u : InvalidValue);
                        value = 0;
                    }
                }
                else
                    value = DispatchEgl(session, task, op, args, guest);
            }

            guest.Write(address + sizeof(uint), MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in value)));
            return 0;
        }
        catch (GuestTransferException ex)
        {
            return -(int)ex.Errno;
        }
    }

    private int DispatchEgl(Session session, FiberTask task, uint op, Span<uint> a, GuestMemory guest)
    {
        switch ((EglOperation)op)
        {
            case EglOperation.Initialize: // Initialize and negotiate the wire ABI.
                if (a[0] == 1) return 1;
                if (a[0] != Abi) return -(int)Errno.EINVAL;
                int renderable = AngleLibrary.Es2Bit | (_angle.SupportsEs3 ? AngleLibrary.Es3Bit : 0);
                guest.Write(a[1], MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in renderable)));
                return (int)Abi;
            case EglOperation.CreateContext: return CreateContext(session, a[0], a[1] != 0, a[2], a[3]);
            case EglOperation.DestroyContext: return DeleteContext(session, a[0]);
            case EglOperation.CreateSurface: return CreateSurface(session, (int)a[0], (int)a[1], a[2]);
            case EglOperation.DestroySurface: return DeleteSurface(session, a[0]);
            case EglOperation.MakeCurrent: return SetCurrent(session, task, new Binding(a[0], a[1], a[2]));
            case EglOperation.Present: return Present(session, task, a[0], a[1], a[2], guest);
            case EglOperation.ResizeSurface: return ResizeSurface(session, task, a[0], (int)a[1], (int)a[2]);
            case EglOperation.FramebufferSize:
            {
                if (_fb == null) return -0x300B;
                var (width, height) = _fb.VisibleSize;
                Span<int> size = [width, height];
                guest.Write(a[0], MemoryMarshal.AsBytes(size));
                return 0;
            }
            case EglOperation.ReleaseThread: return SetCurrent(session, task, default);
            case EglOperation.SwapBuffers: return Present(session, task, a[0], 0, 0, guest);
            case EglOperation.CreateGbmSurface:
                return CreateGbmSurface(session, task, (int)a[0], (int)a[1], (int)a[2], a[3], guest);
            case EglOperation.ImportImage: return ImportImage(session, task, a);
            case EglOperation.DestroyImage: return DestroyImage(session, a[0]);
            case EglOperation.ImageRenderbuffer: return BindImageRenderbuffer(session, task, a[0], a[1]);
            case EglOperation.ImageTexture: return BindImageTexture(session, task, a[0], a[1]);
            case EglOperation.PresentWaylandGpu: return PresentWaylandGpu(session, task, a[0], (int)a[1], a[2]);
            default: return -(int)Errno.ENOTTY;
        }
    }

    private int CreateContext(Session session, uint share, bool managed, uint version, uint flags)
    {
        if (session.Contexts.Count >= MaxObjects) return -0x3003; // EGL_BAD_ALLOC
        if (version == 0) version = 2; // Older guest libraries leave this word zero.
        if (version is not (2 or 3) || (version == 3 && !_angle.SupportsEs3)) return -0x3009;
        if ((flags & ~1u) != 0) return -0x3004;
        ShareGroup? shareGroup = null;
        nint shared = 0;
        if (share != 0)
        {
            if (!session.Contexts.TryGetValue(share, out var other) || other.Deleted) return -0x3006;
            if (managed || other.Managed != null) return -0x3009;
            shared = other.Handle;
            shareGroup = other.Shared;
        }
        if (managed)
        {
            if (_managedOwner != null) return -0x3002;
            shared = _kms?.TextureContext() ?? 0;
            if (shared == 0) return -0x3009;
        }
        int* attributes = stackalloc int[]
        {
            0x3098, managed ? 3 : (int)version, 0x30FB, 0, 0x30FC, (int)flags,
            0x3483, 0, 0x3453, 1, 0x3038,
        };
        var handle = _angle.CreateContext(_angle.Display, Config, shared, attributes);
        if (handle == 0) return AngleError();
        var id = session.NextId++;
        session.Contexts.Add(id, new Context(handle, version, shareGroup ?? new ShareGroup())
            { Managed = managed ? new ManagedFramebufferState(_kms!) : null });
        if (managed) _managedOwner = session;
        return (int)id;
    }

    private int CreateSurface(Session session, int width, int height, uint kind)
    {
        if (session.Surfaces.Count >= MaxObjects) return -0x3003;
        if (!ValidSize(width, height) || kind > FbdevPbuffer) return -0x3004; // EGL_BAD_ATTRIBUTE
        if (kind is Fbdev or FbdevPbuffer && _fb == null) return -0x300B;
        if (!FitsSurfaceBudget(width, height)) return -0x3003;
        var handle = NewPbuffer(width, height);
        if (handle == 0) return AngleError();
        var id = session.NextId++;
        session.Surfaces.Add(id, new Surface(handle, width, height, kind));
        return (int)id;
    }

    private int DeleteContext(Session session, uint id)
    {
        if (!session.Contexts.TryGetValue(id, out var context) || context.Deleted) return -0x3006;
        context.Deleted = true;
        if (!session.Bindings.Values.Any(binding => binding.Context == id))
            FreeContext(session, id);
        return 0;
    }

    private int DeleteSurface(Session session, uint id)
    {
        if (!session.Surfaces.TryGetValue(id, out var surface) || surface.Deleted) return -0x300D;
        surface.Deleted = true;
        if (!session.Bindings.Values.Any(binding => binding.Draw == id || binding.Read == id))
            FreeSurface(session, id);
        return 0;
    }

    private void FreeContext(Session session, uint id)
    {
        if (session.Contexts.Remove(id, out var ctx))
        {
            _angle.DestroyContext(_angle.Display, ctx.Handle);
            ctx.Managed?.Dispose();
            if (!session.Contexts.Values.Any(other => ReferenceEquals(ctx.Shared, other.Shared)))
                foreach (var mapping in ctx.Shared.BufferMappings.Values)
                    mapping.Release();
        }
    }

    private void FreeSurface(Session session, uint id)
    {
        if (!session.Surfaces.Remove(id, out var surface)) return;
        if (surface.Buffers is { } buffers) buffers.Dispose();
        else _angle.DestroySurface(_angle.Display, surface.Handle);
    }

    private int SetCurrent(Session session, FiberTask task, Binding binding)
    {
        // A guest thread can exit without eglReleaseThread.
        foreach (var dead in session.Bindings.Keys.Where(thread => thread.Exited).ToArray())
            RemoveBinding(session, dead);

        var previous = session.Bindings.GetValueOrDefault(task);
        if (binding.Context != 0)
        {
            if (!session.Contexts.TryGetValue(binding.Context, out var context) ||
                (context.Deleted && binding.Context != previous.Context)) return -0x3006;
            if (!AvailableSurface(session, binding.Draw, previous) ||
                !AvailableSurface(session, binding.Read, previous)) return -0x300D;
            foreach (var (otherTask, other) in session.Bindings)
            {
                if (otherTask == task) continue;
                if (other.Context == binding.Context ||
                    (binding.Draw != 0 && (other.Draw == binding.Draw || other.Read == binding.Draw)) ||
                    (binding.Read != 0 && (other.Draw == binding.Read || other.Read == binding.Read)))
                    return -0x3002; // EGL_BAD_ACCESS
            }
        }
        else if (binding.Draw != 0 || binding.Read != 0) return -0x3009;

        if (!Bind(session, binding)) return AngleError();
        if (binding.Context == 0)
            session.Bindings.Remove(task);
        else
            session.Bindings[task] = binding;
        CollectDeleted(session, previous);
        return 0;
    }

    private static bool AvailableSurface(Session session, uint id, Binding previous) =>
        id == 0 || session.Surfaces.TryGetValue(id, out var surface) &&
        (!surface.Deleted || id == previous.Draw || id == previous.Read);

    private void RemoveBinding(Session session, FiberTask task)
    {
        if (!session.Bindings.Remove(task, out var binding)) return;
        if (_hostSession == session && _hostBinding == binding)
            Unbind();
        CollectDeleted(session, binding);
    }

    private void CollectDeleted(Session session, Binding binding)
    {
        if (session.Contexts.TryGetValue(binding.Context, out var context) && context.Deleted &&
            !session.Bindings.Values.Any(item => item.Context == binding.Context))
            FreeContext(session, binding.Context);

        CollectSurface(session, binding.Draw);
        if (binding.Read != binding.Draw) CollectSurface(session, binding.Read);
    }

    private void CollectSurface(Session session, uint id)
    {
        if (session.Surfaces.TryGetValue(id, out var surface) && surface.Deleted &&
            !session.Bindings.Values.Any(item => item.Draw == id || item.Read == id))
            FreeSurface(session, id);
    }

    // The container scheduler executes device ioctls and closes on its runtime thread.
    // Restore the appropriate EGL binding when it switches between guest tasks.
    private bool Bind(Session session, Binding binding)
    {
        if (_hostSession == session && _hostBinding == binding) return true;
        if (binding.Context == 0) return Unbind();
        var draw = binding.Draw == 0 ? 0 : session.Surfaces[binding.Draw].Handle;
        var read = binding.Read == 0 ? 0 : session.Surfaces[binding.Read].Handle;
        if (_angle.MakeCurrent(_angle.Display, draw, read, session.Contexts[binding.Context].Handle) == 0)
            return false;
        _hostSession = session;
        _hostBinding = binding;
        return true;
    }

    private bool Unbind()
    {
        if (_hostSession == null) return true;
        if (_angle.MakeCurrent(_angle.Display, 0, 0, 0) == 0) return false;
        _hostSession = null;
        _hostBinding = default;
        return true;
    }

    private int ResizeSurface(Session session, FiberTask task, uint id, int width, int height)
    {
        if (!session.Surfaces.TryGetValue(id, out var surface) || surface.Kind is Pbuffer or FbdevPbuffer or Gbm)
            return -0x300D;
        if (!ValidSize(width, height)) return -0x3004;
        if (!FitsSurfaceBudget(width, height, surface)) return -0x3003;
        if (session.Bindings.Any(entry => entry.Key != task && (entry.Value.Draw == id || entry.Value.Read == id)))
            return -0x3002;
        if (width == surface.Width && height == surface.Height) return 0;
        var old = surface.Handle;
        var replacement = NewPbuffer(width, height);
        if (replacement == 0) return AngleError();
        if (_hostSession == session && (_hostBinding.Draw == id || _hostBinding.Read == id))
        {
            var binding = _hostBinding;
            if (!Unbind())
            {
                _angle.DestroySurface(_angle.Display, replacement);
                return AngleError();
            }
            surface.Handle = replacement;
            if (!Bind(session, binding))
            {
                var error = AngleError();
                surface.Handle = old;
                _angle.DestroySurface(_angle.Display, replacement);
                return error;
            }
        }
        surface.Handle = replacement;
        surface.Width = width;
        surface.Height = height;
        _angle.DestroySurface(_angle.Display, old);
        return 0;
    }

    private nint NewPbuffer(int width, int height)
    {
        int* attributes = stackalloc int[] { 0x3057, width, 0x3056, height, 0x3038 };
        return _angle.CreatePbufferSurface(_angle.Display, Config, attributes);
    }

    private int PresentWaylandGpu(Session session, FiberTask task, uint id, int fd, uint pitch)
    {
        if (_kms == null || !session.Surfaces.TryGetValue(id, out var surface) || surface.Kind != Wayland ||
            !session.Bindings.TryGetValue(task, out var binding) || binding.Draw != id)
            return -0x300D;
        var file = task.Process.Syscalls.GetFD(fd, task);
        if (DrmCardInode.GetGpuPrimeBuffer(file, (uint)surface.Width, (uint)surface.Height, pitch, 0x34325258)
                is not KmsEglOutput.TextureBo buffer || buffer.Owner != _kms || buffer.InUse)
            return -0x300C;
        if (!Bind(session, binding)) return AngleError();
        if (!_kms.CopyWaylandFrame(surface.Handle, buffer)) return -0x3002;
        return _angle.SwapBuffers(_angle.Display, surface.Handle) != 0 ? 0 : AngleError();
    }

    private int Present(Session session, FiberTask task, uint id, uint output, uint length, GuestMemory guest)
    {
        if (!session.Surfaces.TryGetValue(id, out var surface)) return -0x300D;
        if (!session.Bindings.TryGetValue(task, out var binding) || binding.Draw != id)
            return -0x300D;
        if (!Bind(session, binding)) return AngleError();
        if (surface.Buffers is { } buffers) return SwapGbmSurface(session, binding, surface, buffers);
        if (surface.Kind != Pbuffer)
        {
            var size = surface.Width * surface.Height * 4;
            if (surface.Kind == Wayland && (output == 0 || length < size)) return -0x300C;
            var pixels = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                // glReadPixels reads the EGL read surface, while swap presents the draw surface.
                if (binding.Read != id && !Bind(session, binding with { Read = id })) return AngleError();
                ReadFrame(session.Contexts[binding.Context], surface, pixels.AsSpan(0, size));
                if (!Bind(session, binding)) return AngleError();
                if (surface.Kind is Fbdev or FbdevPbuffer)
                {
                    if (!_fb!.TryPresentRgba(pixels.AsSpan(0, size), surface.Width, surface.Height,
                            scale: surface.Kind == FbdevPbuffer))
                        return -0x300B;
                }
                else
                {
                    var stride = surface.Width * 4;
                    var row = ArrayPool<byte>.Shared.Rent(stride);
                    try
                    {
                        for (var y = 0; y < surface.Height; y++)
                        {
                            var src = pixels.AsSpan((surface.Height - 1 - y) * stride, stride);
                            var dst = row.AsSpan(0, stride);
                            for (var x = 0; x < stride; x += 4)
                            {
                                dst[x] = src[x + 2];
                                dst[x + 1] = src[x + 1];
                                dst[x + 2] = src[x];
                                dst[x + 3] = 0xff;
                            }
                            var offset = (ulong)output + (ulong)(y * stride);
                            if (offset > uint.MaxValue) return -0x300C;
                            guest.Write((uint)offset, dst);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(row); }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(pixels); }
        }
        return _angle.SwapBuffers(_angle.Display, surface.Handle) != 0 ? 0 : AngleError();
    }

    private void ReadFrame(Context context, Surface surface, Span<byte> pixels)
    {
        var bindFramebuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindFramebuffer");
        var read = (delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void>)Gl("glReadPixels");
        var store = (delegate* unmanaged[Cdecl]<uint, int, void>)Gl("glPixelStorei");
        var es3 = context.Version >= 3 || context.Managed != null;
        var target = es3 ? ReadFramebuffer : 0x8D40;
        var framebuffer = (uint)GetInteger(es3 ? ReadFramebufferBinding : 0x8CA6);
        if (framebuffer != 0) bindFramebuffer(target, 0);

        Span<(uint Parameter, int Previous)> pack = stackalloc (uint, int)[es3 ? 4 : 1];
        pack[0] = (PackAlignment, GetInteger(PackAlignment));
        if (es3)
        {
            pack[1] = (0x0D02, GetInteger(0x0D02)); // PACK_ROW_LENGTH
            pack[2] = (0x0D03, GetInteger(0x0D03)); // PACK_SKIP_ROWS
            pack[3] = (0x0D04, GetInteger(0x0D04)); // PACK_SKIP_PIXELS
        }
        var bindBuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindBuffer");
        var pixelBuffer = es3 ? (uint)GetInteger(PixelPackBufferBinding) : 0;
        var readBuffer = es3 ? (uint)GetInteger(0x0C02) : 0x0405; // READ_BUFFER defaults to BACK
        var selectReadBuffer = es3 ? (delegate* unmanaged[Cdecl]<uint, void>)Gl("glReadBuffer") : null;
        try
        {
            foreach (var (parameter, previous) in pack)
            {
                var desired = parameter == PackAlignment ? 4 : 0;
                if (previous != desired) store(parameter, desired);
            }
            if (pixelBuffer != 0) bindBuffer(PixelPackBuffer, 0);
            if (readBuffer != 0x0405) selectReadBuffer(0x0405);
            // A failed read must never copy another frame from a pooled buffer into a guest.
            pixels.Clear();
            fixed (byte* ptr = pixels)
                read(0, 0, surface.Width, surface.Height, 0x1908, 0x1401, ptr);
        }
        finally
        {
            if (readBuffer != 0x0405) selectReadBuffer(readBuffer);
            if (pixelBuffer != 0) bindBuffer(PixelPackBuffer, pixelBuffer);
            foreach (var (parameter, previous) in pack)
                if (previous != (parameter == PackAlignment ? 4 : 0)) store(parameter, previous);
            if (framebuffer != 0) bindFramebuffer(target, framebuffer);
        }
    }

    private int AngleError()
    {
        var error = _angle.GetError();
        return error == 0x3000 ? -0x3002 : -error;
    }

    private bool FitsSurfaceBudget(int width, int height, Surface? replaced = null)
    {
        var allocated = _sessions.Sum(session => session.Surfaces.Values.Sum(surface =>
            (long)surface.Width * surface.Height * (surface.Buffers?.Slots.Length ?? 1)));
        if (replaced != null) allocated -= (long)replaced.Width * replaced.Height;
        return allocated + (long)width * height <= MaxSurfacePixels;
    }

    private static bool ValidSize(int width, int height) =>
        width is > 0 and <= FramebufferDevice.DimensionLimit &&
        height is > 0 and <= FramebufferDevice.DimensionLimit;

    private void Close(Session session)
    {
        using (_angle.EnterDisplayLock())
        {
            if (!_sessions.Remove(session)) return;
            if (_hostSession == session) Unbind();
            foreach (var id in session.Contexts.Keys.ToArray())
                FreeContext(session, id);
            foreach (var image in session.Images.Values)
                image.Release();
            session.Images.Clear();
            foreach (var id in session.Surfaces.Keys.ToArray())
                FreeSurface(session, id);
            session.Contexts.Clear();
            session.Surfaces.Clear();
            session.Bindings.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using (_angle.EnterDisplayLock())
        {
            foreach (var session in _sessions.ToArray())
                Close(session);
            if (HasKmsOutput) _kms?.Dispose();
        }
        _angle.Release();
    }
}
