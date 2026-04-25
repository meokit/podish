namespace Fiberish.Core.Net;

public sealed class PrivateNetstackStream : INetstackStream
{
    private readonly LoopbackNetNamespace.TcpStreamSocket _socket;

    public PrivateNetstackStream(LoopbackNetNamespace.TcpStreamSocket socket)
    {
        _socket = socket;
    }

    public bool IsClosed => _socket.State is 0 or >= 6;

    public bool CanRead => _socket.CanRead;
    public bool CanWrite => _socket.CanWrite;
    public bool MayRead => _socket.MayRead;
    public bool MayWrite => _socket.MayWrite;

    public int Read(Span<byte> buffer)
    {
        return _socket.Receive(buffer);
    }

    public int Write(ReadOnlySpan<byte> buffer)
    {
        return _socket.Send(buffer);
    }

    public void CloseWrite()
    {
        _socket.CloseWrite();
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}