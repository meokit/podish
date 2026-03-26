using Fiberish.Core;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal sealed class WaylandServerSession
{
    private readonly WaylandConnection _connection;
    private readonly WaylandClient _client;
    private readonly ILogger _logger;

    public WaylandServerSession(VirtualDaemonConnection connection, WaylandServer server, ILogger logger)
    {
        _connection = new WaylandConnection(connection);
        _logger = logger;
        _client = server.CreateClient(message =>
        {
            WaylandMessageHeader header = WaylandMessageHeader.Decode(message.Buffer);
            var (interfaceName, messageName) = DescribeOutgoing(header);
            _logger.LogDebug(
                "Wayland send object={ObjectId} interface={Interface} opcode={Opcode} message={Message} size={Size} fds={FdCount}",
                header.ObjectId, interfaceName, header.Opcode, messageName, header.Size, message.Fds?.Count ?? 0);
            return _connection.SendAsync(message);
        });
    }

    public WaylandClient Client => _client;

    public async Task RunAsync()
    {
        _connection.RawConnection.File.Flags &= ~FileFlags.O_NONBLOCK;
        _logger.LogDebug("Wayland session entering receive loop");
        while (true)
        {
            WaylandIncomingMessage? message = await _connection.ReceiveAsync();
            if (message == null)
            {
                _logger.LogDebug("Wayland session got EOF");
                return;
            }

            int expectedFdCount = _client.GetExpectedFdCount(message.Header);
            if (expectedFdCount > 0)
                message = message with { Fds = _connection.TakePendingFds(expectedFdCount) };

            var (interfaceName, messageName) = DescribeIncoming(message.Header);
            _logger.LogDebug(
                "Wayland recv object={ObjectId} interface={Interface} opcode={Opcode} message={Message} size={Size} fds={FdCount}",
                message.Header.ObjectId, interfaceName, message.Header.Opcode, messageName, message.Header.Size, message.Fds.Count);

            if (string.Equals(interfaceName, WlRegistryProtocol.InterfaceName, StringComparison.Ordinal) &&
                message.Header.Opcode == 0)
            {
                var bind = WlRegistryProtocol.DecodeBind(message.Body, message.Fds);
                _logger.LogDebug(
                    "Wayland registry bind global={GlobalName} interface={BindInterface} version={Version} newId={NewId}",
                    bind.Name, bind.Interface, bind.Version, bind.Id);
            }

            try
            {
                await _client.ProcessMessageAsync(message);
                _logger.LogDebug("Wayland dispatch completed object={ObjectId} interface={Interface} opcode={Opcode} message={Message}",
                    message.Header.ObjectId, interfaceName, message.Header.Opcode, messageName);
            }
            catch (WaylandProtocolException ex)
            {
                _logger.LogWarning(ex, "Wayland protocol error object={ObjectId} code={Code}", ex.ObjectId, ex.ErrorCode);
                await _client.SendProtocolErrorAsync(ex.ObjectId, ex.ErrorCode, ex.Message);
                return;
            }
        }
    }

    private (string Interface, string Message) DescribeIncoming(WaylandMessageHeader header)
    {
        if (!_client.Objects.TryGetValue(header.ObjectId, out WaylandResource? resource) || resource is null)
            return ("<unknown>", "<unknown>");

        var messageName = header.Opcode < resource.Requests.Count
            ? resource.Requests[header.Opcode].Name
            : "<unknown>";
        return (resource.InterfaceName, messageName);
    }

    private (string Interface, string Message) DescribeOutgoing(WaylandMessageHeader header)
    {
        if (!_client.Objects.TryGetValue(header.ObjectId, out WaylandResource? resource) || resource is null)
            return ("<unknown>", "<unknown>");

        return (resource.InterfaceName, $"event#{header.Opcode}");
    }
}
