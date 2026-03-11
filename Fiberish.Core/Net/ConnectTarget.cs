using System.Net;

namespace Fiberish.Core.Net;

public readonly record struct ConnectTarget(
    IPAddress Address,
    int Port,
    TransportProtocol Protocol);