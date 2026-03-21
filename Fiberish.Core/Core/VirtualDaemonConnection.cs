using Fiberish.VFS;

namespace Fiberish.Core;

public sealed class VirtualDaemonConnection : IDisposable
{
    internal VirtualDaemonConnection(VirtualDaemonRuntime runtime, LinuxFile file, LinuxFile listenerFile)
    {
        Runtime = runtime;
        File = file;
        ListenerFile = listenerFile;
    }

    public VirtualDaemonRuntime Runtime { get; }
    public LinuxFile File { get; }
    public LinuxFile ListenerFile { get; }

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, int flags = 0)
    {
        if (!File.TryGetSocketDataOps(out var ops)) return 0;
        return await ops.SendAsync(File, Runtime.Task, buffer, flags);
    }

    public async ValueTask<int> RecvAsync(byte[] buffer, int flags = 0, int maxBytes = -1)
    {
        if (!File.TryGetSocketDataOps(out var ops)) return 0;
        return await ops.RecvAsync(File, Runtime.Task, buffer, flags, maxBytes);
    }

    public async ValueTask<int> SendMsgAsync(byte[] buffer, List<LinuxFile>? fds = null, int flags = 0,
        object? endpoint = null)
    {
        if (!File.TryGetSocketDataOps(out var ops)) return 0;
        return await ops.SendMsgAsync(File, Runtime.Task, buffer, fds, flags, endpoint);
    }

    public async ValueTask<RecvMessageResult> RecvMsgAsync(byte[] buffer, int flags = 0, int maxBytes = -1)
    {
        if (!File.TryGetSocketDataOps(out var ops)) return new RecvMessageResult(0);
        return await ops.RecvMsgAsync(File, Runtime.Task, buffer, flags, maxBytes);
    }

    public int Shutdown(int how)
    {
        if (!File.TryGetSocketEndpointOps(out var ops)) return 0;
        return ops.Shutdown(File, Runtime.Task, how);
    }

    public void Dispose()
    {
        File.Close();
    }
}
