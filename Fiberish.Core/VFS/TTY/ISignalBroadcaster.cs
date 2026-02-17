namespace Fiberish.Core.VFS.TTY;

public interface ISignalBroadcaster
{
    /// <summary>
    ///     Sends a signal to a process group.
    /// </summary>
    /// <param name="pgid">The process group ID.</param>
    /// <param name="signal">The signal number.</param>
    void SignalProcessGroup(int pgid, int signal);

    /// <summary>
    ///     Sends a signal to the current foreground task/process if applicable.
    /// </summary>
    /// <param name="signal">The signal number.</param>
    void SignalForegroundTask(int signal);
}