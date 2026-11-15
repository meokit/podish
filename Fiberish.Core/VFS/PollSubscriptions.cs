namespace Fiberish.VFS;

internal sealed class PollSubscriptions(IDisposable? first, IDisposable? second) : IDisposable
{
    public void Dispose()
    {
        Interlocked.Exchange(ref first, null)?.Dispose();
        Interlocked.Exchange(ref second, null)?.Dispose();
    }
}
