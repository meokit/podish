using System.Net.Sockets;
using Fiberish.Core.Net;

namespace Podish.Core.Networking;

public sealed class RelaySession : IDisposable
{
    public RelaySession(long id, Socket hostSocket, INetstackStream guestStream, ContainerNetworkContext context)
    {
        Id = id;
        HostSocket = hostSocket;
        GuestStream = guestStream;
        Context = context;
        
        HostToGuestBuffer = new byte[64 * 1024];
        GuestToHostBuffer = new byte[64 * 1024];
        
        HostReceiveArgs = new SocketAsyncEventArgs();
        HostReceiveArgs.SetBuffer(HostToGuestBuffer);
        
        HostSendArgs = new SocketAsyncEventArgs();
        HostSendArgs.SetBuffer(GuestToHostBuffer);
    }

    public long Id { get; }
    public Socket HostSocket { get; }
    public INetstackStream GuestStream { get; }
    public ContainerNetworkContext Context { get; }

    public byte[] HostToGuestBuffer { get; }
    public int HostToGuestCount { get; set; }
    public int HostToGuestOffset { get; set; }

    public byte[] GuestToHostBuffer { get; }
    public int GuestToHostCount { get; set; }
    public int GuestToHostOffset { get; set; }

    public bool GuestConnected { get; set; }
    public bool HostReadClosed { get; set; }
    public bool HostWriteClosed { get; set; }
    public bool GuestReadClosed { get; set; }
    public bool GuestWriteClosed { get; set; }

    public SocketAsyncEventArgs HostReceiveArgs { get; }
    public SocketAsyncEventArgs HostSendArgs { get; }
    
    public bool HostReceivePending { get; set; }
    public bool HostSendPending { get; set; }
    
    public bool HostReceiveEventBound { get; set; }
    public bool HostSendEventBound { get; set; }
    
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        try { HostSocket.Dispose(); } catch { }
        try { GuestStream.Dispose(); } catch { }
        HostReceiveArgs.Dispose();
        HostSendArgs.Dispose();
    }
}
