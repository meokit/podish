using Podish.Core.Graphics;

namespace Podish.Wayland;

internal sealed class LinuxExplicitSyncResource(WaylandClient client, uint objectId, uint version)
    : WaylandResource(client, objectId, version, ZwpLinuxExplicitSynchronizationV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests =>
        ZwpLinuxExplicitSynchronizationV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            await Client.DeleteIdAsync(ObjectId);
            return;
        }

        var request = ZwpLinuxExplicitSynchronizationV1Protocol.DecodeGetSynchronization(message.Body, message.Fds);
        var surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
        if (surface.ExplicitSync != null)
            throw new WaylandProtocolException(ObjectId, (uint)ZwpLinuxExplicitSynchronizationV1Error.SynchronizationExists,
                "The surface already has a synchronization object.");
        surface.ExplicitSync = Client.Register(new LinuxSurfaceSyncResource(Client, request.Id, Version, surface));
    }
}

internal sealed class LinuxSurfaceSyncResource(
    WaylandClient client, uint objectId, uint version, WlSurfaceResource surface)
    : WaylandResource(client, objectId, version, ZwpLinuxSurfaceSynchronizationV1Protocol.InterfaceName)
{
    private VirtioGpuNode.Fence? _acquire;

    public override IReadOnlyList<WaylandMessageMetadata> Requests =>
        ZwpLinuxSurfaceSynchronizationV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            await Client.DeleteIdAsync(ObjectId);
            return;
        }
        if (surface.Destroyed)
            throw Error(ZwpLinuxSurfaceSynchronizationV1Error.NoSurface, "The surface has been destroyed.");

        if (message.Header.Opcode == 1)
        {
            if (_acquire != null)
                throw Error(ZwpLinuxSurfaceSynchronizationV1Error.DuplicateFence, "An acquire fence is already pending.");
            var request = ZwpLinuxSurfaceSynchronizationV1Protocol.DecodeSetAcquireFence(message.Body, message.Fds);
            // The managed fence outlives the received descriptor. The renderer's
            // context retires it even after the sender and the connection close their fds.
            _acquire = VirtioGpuNode.GetFence(request.Fd)
                ?? throw Error(ZwpLinuxSurfaceSynchronizationV1Error.InvalidFence, "A virtio-gpu sync_file descriptor is required.");
        }
        else
        {
            if (surface.PendingExplicitRelease != null)
                throw Error(ZwpLinuxSurfaceSynchronizationV1Error.DuplicateRelease, "A buffer release is already pending.");
            var request = ZwpLinuxSurfaceSynchronizationV1Protocol.DecodeGetRelease(message.Body, message.Fds);
            surface.PendingExplicitRelease = Client.Register(new LinuxBufferReleaseResource(Client, request.Release));
        }
    }

    internal async ValueTask WaitForAcquireAsync(WlBufferResource? attachedBuffer, bool requireFence)
    {
        if (Client.IsDisconnecting)
            throw new IOException("Wayland connection is closing.");
        var fence = _acquire;
        _acquire = null;
        if (fence == null && surface.PendingExplicitRelease == null && !requireFence)
            return;
        if (attachedBuffer == null)
            throw Error(ZwpLinuxSurfaceSynchronizationV1Error.NoBuffer, "Attach a buffer before committing synchronization state.");

        if (fence == null && requireFence)
            throw Error(ZwpLinuxSurfaceSynchronizationV1Error.InvalidFence, "This PRIME buffer requires an acquire fence.");
        if (fence == null || fence.Signaled)
            return;
        var task = Client.ConnectionTask
            ?? throw new InvalidOperationException("The Wayland connection has no scheduler task.");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fence.Ready.RegisterCancelable(() => ready.TrySetResult(true), task);
        var timer = task.CommonKernel.ScheduleTimer(10_000, () => ready.TrySetResult(fence.Signaled));
        void OnDisconnecting() => ready.TrySetResult(false);
        Client.Disconnecting += OnDisconnecting;
        try
        {
            if (!await ready.Task || Client.IsDisconnecting)
                throw new IOException("GPU acquire fence did not signal.");
        }
        finally
        {
            Client.Disconnecting -= OnDisconnecting;
            timer.Cancel();
        }
    }

    internal WaylandProtocolException Error(ZwpLinuxSurfaceSynchronizationV1Error code, string message) =>
        new(ObjectId, (uint)code, message);

    public override void Destroy()
    {
        _acquire = null;
        if (surface.ExplicitSync == this)
            surface.ExplicitSync = null;
        base.Destroy();
    }
}

internal sealed class LinuxBufferReleaseResource(WaylandClient client, uint objectId)
    : WaylandResource(client, objectId, 1, ZwpLinuxBufferReleaseV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwpLinuxBufferReleaseV1Protocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message) =>
        throw new WaylandProtocolException(ObjectId, 0, "The buffer release object has no requests.");

    internal async ValueTask CompleteAsync()
    {
        if (Destroyed)
            return;
        await ZwpLinuxBufferReleaseV1EventWriter.ImmediateReleaseAsync(Client, ObjectId);
        Destroy();
        await Client.DeleteIdAsync(ObjectId);
    }
}
