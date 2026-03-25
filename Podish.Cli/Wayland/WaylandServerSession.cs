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
            _logger.LogDebug("Wayland send object={ObjectId} opcode={Opcode} size={Size} fds={FdCount}",
                header.ObjectId, header.Opcode, header.Size, message.Fds?.Count ?? 0);
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

            _logger.LogDebug("Wayland recv object={ObjectId} opcode={Opcode} size={Size} fds={FdCount}",
                message.Header.ObjectId, message.Header.Opcode, message.Header.Size, message.Fds.Count);

            try
            {
                await _client.ProcessMessageAsync(message);
                _logger.LogDebug("Wayland dispatch completed object={ObjectId} opcode={Opcode}",
                    message.Header.ObjectId, message.Header.Opcode);
            }
            catch (WaylandProtocolException ex)
            {
                _logger.LogWarning(ex, "Wayland protocol error object={ObjectId} code={Code}", ex.ObjectId, ex.ErrorCode);
                await _client.SendProtocolErrorAsync(ex.ObjectId, ex.ErrorCode, ex.Message);
                return;
            }
        }
    }
}
