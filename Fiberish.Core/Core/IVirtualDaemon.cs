namespace Fiberish.Core;

public interface IVirtualDaemon
{
    string Name { get; }
    string UnixPath { get; }

    void OnStart(VirtualDaemonContext context);
    void OnSignal(VirtualDaemonContext context, int signo);
    void OnStop(VirtualDaemonContext context);
}
