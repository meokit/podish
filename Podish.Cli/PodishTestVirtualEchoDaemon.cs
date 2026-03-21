using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Cli;

internal sealed class PodishTestVirtualEchoDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;

    public PodishTestVirtualEchoDaemon(string unixPath, ILogger logger)
    {
        UnixPath = unixPath;
        _logger = logger;
    }

    public string Name => "podish-test-echo";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        ScheduleAcceptLoop(context);
    }

    public void OnSignal(VirtualDaemonContext context, int signo)
    {
        _logger.LogDebug("Virtual echo daemon received signal {Signal}", signo);
        context.Exit(128 + signo);
    }

    public void OnStop(VirtualDaemonContext context)
    {
        _logger.LogDebug("Virtual echo daemon stopping path={Path}", UnixPath);
    }

    private void ScheduleAcceptLoop(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;

            while (!ctx.Task.Exited)
            {
                if (ParentExited(ctx))
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
                    _logger.LogDebug("Virtual echo daemon accept returned rc={Rc}", rc);
                    return;
                }

                ctx.Schedule(_ => new ValueTask(HandleClientAsync(connection)));
            }
        });
    }

    private static bool ParentExited(VirtualDaemonContext context)
    {
        var ppid = context.Process.PPID;
        if (ppid <= 0)
            return false;

        var parent = context.Scheduler.GetProcess(ppid);
        return parent == null || parent.State is ProcessState.Zombie or ProcessState.Dead;
    }

    private async Task HandleClientAsync(VirtualDaemonConnection connection)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var bytes = await connection.RecvAsync(buffer, 0, buffer.Length);
                if (bytes <= 0)
                    return;

                var written = 0;
                while (written < bytes)
                {
                    var sent = await connection.SendAsync(buffer.AsMemory(written, bytes - written), 0);
                    if (sent <= 0)
                        return;
                    written += sent;
                }
            }
        }
        finally
        {
            connection.Dispose();
        }
    }
}
