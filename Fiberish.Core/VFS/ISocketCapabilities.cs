using System.Net;
using Fiberish.Core;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public record AcceptedSocketResult(int Rc, Inode? Inode, EndPoint? PeerEndPoint = null, byte[]? PeerUnixAddressRaw = null);
public record SocketAddressResult(EndPoint? EndPoint = null, byte[]? UnixAddressRaw = null);
public record RecvMessageResult(int BytesRead, List<LinuxFile>? Fds = null, EndPoint? SourceEndPoint = null, byte[]? SourceSunPathRaw = null);

public interface ISocketEndpointOps
{
    int Bind(LinuxFile file, FiberTask task, object endpoint);
    ValueTask<int> ConnectAsync(LinuxFile file, FiberTask task, object endpoint);
    int Listen(LinuxFile file, FiberTask task, int backlog);
    ValueTask<AcceptedSocketResult> AcceptAsync(LinuxFile file, FiberTask task, int flags);
    SocketAddressResult GetSockName(LinuxFile file, FiberTask task);
    SocketAddressResult GetPeerName(LinuxFile file, FiberTask task);
    int Shutdown(LinuxFile file, FiberTask task, int how);
}

public interface ISocketDataOps
{
    ValueTask<int> SendAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags);
    ValueTask<int> SendToAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags, object endpoint);
    ValueTask<int> RecvAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1);
    ValueTask<RecvMessageResult> RecvFromAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1);
    ValueTask<int> SendMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, List<LinuxFile>? fds, int flags, object? endpoint);
    ValueTask<RecvMessageResult> RecvMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1);
}

public interface ISocketOptionOps
{
    int SetSocketOption(LinuxFile file, FiberTask task, int level, int optname, ReadOnlySpan<byte> optval);
    int GetSocketOption(LinuxFile file, FiberTask task, int level, int optname, Span<byte> optval, out int written);
}

public static class SocketCapabilityExtensions
{
    public static bool TryGetSocketEndpointOps(this LinuxFile file, out ISocketEndpointOps ops)
    {
        if (file.OpenedInode is ISocketEndpointOps eOps)
        {
            ops = eOps;
            return true;
        }
        ops = null!;
        return false;
    }

    public static bool TryGetSocketDataOps(this LinuxFile file, out ISocketDataOps ops)
    {
        if (file.OpenedInode is ISocketDataOps dOps)
        {
            ops = dOps;
            return true;
        }
        ops = null!;
        return false;
    }

    public static bool TryGetSocketOptionOps(this LinuxFile file, out ISocketOptionOps ops)
    {
        if (file.OpenedInode is ISocketOptionOps oOps)
        {
            ops = oOps;
            return true;
        }
        ops = null!;
        return false;
    }
}
