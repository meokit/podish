using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Wayland;
using Podish.Wayland;

namespace Podish.Cli;

internal sealed class PodishWaylandVirtualDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;
    private readonly int _ownerPid;
    private readonly WaylandServer _server;

    public PodishWaylandVirtualDaemon(string unixPath, int ownerPid, ILoggerFactory loggerFactory)
    {
        UnixPath = unixPath;
        _ownerPid = ownerPid;
        _logger = loggerFactory.CreateLogger<PodishWaylandVirtualDaemon>();
        _server = new WaylandServer();
    }

    public string Name => "podish-wayland";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;

            while (!ctx.Task.Exited)
            {
                if (OwnerExited(ctx))
                {
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
                    _logger.LogDebug("Wayland accept returned rc={Rc}", rc);
                    return;
                }

                _logger.LogInformation("Wayland accepted client connection path={Path}", UnixPath);

                if (connection.File.OpenedInode is UnixSocketInode acceptedSocket)
                {
                    UnixCredentials? peerCredentials = acceptedSocket.GetPeerCredentials();
                    if (peerCredentials != null)
                        acceptedSocket.SetSendCredentialsOverride(peerCredentials);
                }

                ctx.ScheduleChild(childCtx =>
                {
                    connection.BindTask(childCtx.Task);
                    _logger.LogDebug("Wayland scheduling child task tid={Tid}", childCtx.Task.TID);
                    return new ValueTask(HandleClientAsync(connection));
                });
            }
        });
    }

    public void OnSignal(VirtualDaemonContext context, int signo)
    {
        _logger.LogDebug("Wayland daemon received signal {Signal}", signo);
        context.Exit(128 + signo);
    }

    public void OnStop(VirtualDaemonContext context)
    {
        _logger.LogDebug("Wayland daemon stopping path={Path}", UnixPath);
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
            using (connection)
            {
                _logger.LogInformation("Wayland client handler starting");
                var session = new WaylandServerSession(connection, _server, _logger);
                await session.RunAsync();
                _logger.LogInformation("Wayland client handler completed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wayland client handler failed");
            throw;
        }
    }
}
