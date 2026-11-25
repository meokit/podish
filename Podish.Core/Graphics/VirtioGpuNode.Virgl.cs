using Fiberish.Core;
using Fiberish.Native;

namespace Podish.Core.Graphics;

internal sealed partial class VirtioGpuNode
{
    private readonly record struct VirglResourceShape(uint Target, uint Width, uint Height, uint Depth,
        uint Layers, uint LastLevel, uint Samples, uint Stride)
    {
        internal bool IsTexturePlane(uint width, uint height, uint pitch) =>
            Target == 2 && Width == width && Height == height && Depth == 1 && Layers == 1 &&
            LastLevel == 0 && Samples <= 1 && (Stride == 0 || Stride == pitch);
    }

    private int AllocateVirglResource(OpenFile open, FiberTask task, uint arg, Span<byte> data)
    {
        if (open.Context is not { IsVirgl: true } context) return -(int)Errno.EOPNOTSUPP;
        var shape = new VirglResourceShape(U32(data, 0), U32(data, 12), U32(data, 16), U32(data, 20),
            U32(data, 24), U32(data, 28), U32(data, 32), U32(data, 52));
        if (shape.Target > 8 || shape.Width == 0 || shape.Height == 0 || shape.Depth == 0 ||
            shape.Layers == 0 || U32(data, 40) != 0)
            return Invalid;

        var size = U32(data, 48);
        if (!context.Renderer.CreateVirglResource(context.Id, data, out var id, out var fd))
            return Invalid;

        var resource = new Resource(context, id, size, fd, shape);
        if (size != 0 && (fd < 0 || !resource.CreateBacking(task.Process.Syscalls)))
        {
            resource.Release();
            return Invalid;
        }

        var handle = open.NextHandle++;
        Put(data, 40, handle);
        Put(data, 44, id);
        if (!Write(task.CPU, arg, data))
        {
            resource.Release();
            return Fault;
        }
        open.Handles.Add(handle, new Handle(resource));
        return 0;
    }

    private static int TransferVirglResource(OpenFile open, FiberTask task, ReadOnlySpan<byte> data, bool toHost)
    {
        if (open.Context is not { IsVirgl: true } context) return -(int)Errno.EOPNOTSUPP;
        if (!open.Handles.TryGetValue(U32(data, 0), out var entry)) return Missing;

        var resource = entry.Resource;
        if (resource.Virgl is not { } shape || resource.Backing == null || U32(data, 28) > shape.LastLevel ||
            U32(data, 32) >= resource.Size || U32(data, 16) == 0 || U32(data, 20) == 0 || U32(data, 24) == 0)
            return Invalid;
        if (!context.Renderer.TransferVirglResource(context.Id, resource.Id, toHost, data))
            return Invalid;

        var fence = context.CreateFence(0, task.CommonKernel);
        if (fence == null) return Invalid;
        resource.LastFence = fence;
        return 0;
    }
}
