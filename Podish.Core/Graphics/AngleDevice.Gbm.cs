using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.VFS;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const int GbmSlots = 3;

    private sealed class GbmSlot(AngleLibrary angle, KmsEglOutput output, nint surface, int width, int height)
        : DrmScanoutBuffer((uint)width, (uint)height, (uint)width * 4)
    {
        internal readonly nint Surface = surface;
        internal nint Fence;
        internal uint Handle;
        public override bool Ready => Fence != 0;
        public override bool Present() => output.Present(Surface, Fence);

        internal void PrepareForRendering()
        {
            output.DestroyFence(Fence);
            Fence = 0;
        }

        protected override void Destroy()
        {
            PrepareForRendering();
            angle.DestroySurface(angle.Display, Surface);
        }
    }

    private sealed class GbmBuffers(DrmCardInode card, LinuxFile file) : IDisposable
    {
        internal readonly GbmSlot?[] Slots = new GbmSlot[GbmSlots];
        internal int Rendering;

        public void Dispose()
        {
            foreach (var slot in Slots)
            {
                if (slot == null) continue;
                if (slot.Handle != 0) card.UnregisterGpuBuffer(file, slot.Handle);
                slot.Release();
            }
        }
    }

    private int CreateGbmSurface(Session session, FiberTask task, int width, int height, int fd, uint handles,
        GuestMemory guest)
    {
        if (_kms == null || task.Process.Syscalls.GetFD(fd, task) is not { OpenedInode: DrmCardInode card } file)
            return -0x300B; // EGL_BAD_NATIVE_WINDOW
        if (!ValidSize(width, height)) return -0x3004;
        if (session.Surfaces.Count >= MaxObjects || !FitsSurfaceBudget(width, height * GbmSlots))
            return -0x3003;

        var buffers = new GbmBuffers(card, file);
        var added = false;
        try
        {
            Span<uint> ids = stackalloc uint[GbmSlots];
            for (var i = 0; i < GbmSlots; i++)
            {
                var backing = _kms.CreatePbuffer(width, height);
                if (backing == 0) return AngleError();
                var slot = new GbmSlot(_angle, _kms, backing, width, height);
                buffers.Slots[i] = slot;
                slot.Handle = card.RegisterGpuBuffer(file, slot);
                if (slot.Handle == 0) return -0x3003;
                ids[i] = slot.Handle;
            }
            guest.Write(handles, MemoryMarshal.AsBytes(ids));
            var id = session.NextId++;
            session.Surfaces.Add(id, new Surface(buffers.Slots[0]!.Surface, width, height, Gbm)
            {
                Buffers = buffers
            });
            added = true;
            return (int)id;
        }
        finally
        {
            if (!added) buffers.Dispose();
        }
    }

    private int SwapGbmSurface(Session session, Binding binding, Surface surface, GbmBuffers buffers)
    {
        var current = buffers.Slots[buffers.Rendering]!;
        var next = -1;
        for (var i = 0; i < GbmSlots; i++)
        {
            var candidate = (buffers.Rendering + i + 1) % GbmSlots;
            if (candidate != buffers.Rendering && !buffers.Slots[candidate]!.InUse)
            {
                next = candidate;
                break;
            }
        }
        if (next < 0) return -0x3002; // The application must release a GBM front buffer.

        var fence = _kms!.Fence();
        if (fence == 0) return AngleError();

        var replacement = buffers.Slots[next]!;
        surface.Handle = replacement.Surface;
        // The logical draw/read IDs did not change, but their native EGL surfaces did.
        _hostBinding = default;
        if (!Bind(session, binding))
        {
            surface.Handle = current.Surface;
            _hostBinding = binding; // eglMakeCurrent failure preserves the previous binding.
            _kms.DestroyFence(fence);
            return AngleError();
        }

        current.Fence = fence;
        replacement.PrepareForRendering();
        buffers.Rendering = next;
        return (int)current.Handle;
    }
}
