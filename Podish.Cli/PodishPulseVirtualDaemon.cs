using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Pulse;

namespace Podish.Cli;

internal sealed class PodishPulseVirtualDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;
    private readonly int _ownerPid;
    private readonly PulseServerState _serverState;

    public PodishPulseVirtualDaemon(string unixPath, int ownerPid, ILoggerFactory loggerFactory)
    {
        UnixPath = unixPath;
        _ownerPid = ownerPid;
        _logger = loggerFactory.CreateLogger<PodishPulseVirtualDaemon>();
        _serverState = new PulseServerState(loggerFactory);
    }

    public string Name => "podish-pulse";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        ScheduleAcceptLoop(context);
    }

    public void OnSignal(VirtualDaemonContext context, int signo)
    {
        _logger.LogDebug("{Prefix} virtual Pulse daemon received signal {Signal}", PulseServerLogging.Connection, signo);
        context.Exit(128 + signo);
    }

    public void OnStop(VirtualDaemonContext context)
    {
        _serverState.Dispose();
        _logger.LogDebug("{Prefix} virtual Pulse daemon stopping path={Path}", PulseServerLogging.Connection,
            UnixPath);
    }

    private void ScheduleAcceptLoop(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;

            while (!ctx.Task.Exited)
            {
                if (OwnerExited(ctx))
                {
                    _logger.LogDebug(
                        "{Prefix} virtual Pulse daemon exiting because owner pid={OwnerPid} is gone",
                        PulseServerLogging.Connection, _ownerPid);
                    ctx.Exit(0);
                    return;
                }

                var (rc, connection) = await ctx.AcceptAsync();
                if (ctx.Task.Exited)
                    return;
                if (rc == -(int)Errno.EAGAIN)
                {
                    await new SleepAwaitable(25, ctx.Task, ctx.Scheduler);
                    continue;
                }
                if (rc != 0 || connection == null)
                {
                    _logger.LogDebug("{Prefix} accept returned rc={Rc}", PulseServerLogging.Connection, rc);
                    return;
                }

                if (connection.File.OpenedInode is UnixSocketInode acceptedSocket)
                {
                    UnixCredentials? peerCredentials = acceptedSocket.GetPeerCredentials();
                    if (peerCredentials != null)
                        acceptedSocket.SetSendCredentialsOverride(peerCredentials);
                }

                ctx.ScheduleChild(childCtx =>
                {
                    connection.BindTask(childCtx.Task);
                    return new ValueTask(HandleClientAsync(connection));
                });
            }
        });
    }

    private bool OwnerExited(VirtualDaemonContext context)
    {
        if (_ownerPid <= 0)
            return false;

        var owner = context.Scheduler.GetProcess(_ownerPid);
        return owner == null || owner.State is ProcessState.Zombie or ProcessState.Dead;
    }

    private async Task HandleClientAsync(VirtualDaemonConnection connection)
    {
        try
        {
            var session = new PulseServerSession(connection, _serverState,
                _serverState.LoggerFactory.CreateLogger<PulseServerSession>());
            await session.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix} client handler failed", PulseServerLogging.Connection);
            throw;
        }
        finally
        {
            connection.Dispose();
        }
    }
}
