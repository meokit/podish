using System.Net.Sockets;
using Fiberish.Core.Net;

namespace Podish.Core.Networking;

internal enum LoopEventType
{
    HostAccept,
    HostReceive,
    HostSend,
    CommandStop,
    CommandStartPort,
    NamespacePoll,
    CommandStopAck
}

internal sealed class LoopEvent
{
    public required LoopEventType Type { get; init; }
    public Socket? HostSocket { get; init; }
    public SocketAsyncEventArgs? Args { get; init; }
    public ContainerNetworkContext? Context { get; init; }
    public PublishedPortSpec? PortSpec { get; init; }
    public RelaySession? Session { get; init; }
    public TaskCompletionSource? Completion { get; init; }
}
