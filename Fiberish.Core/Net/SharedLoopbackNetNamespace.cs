using System.Threading;

namespace Fiberish.Core.Net;

public sealed class SharedLoopbackNetNamespace
{
    private int _refCount = 1;

    public SharedLoopbackNetNamespace(LoopbackNetNamespace @namespace)
    {
        Namespace = @namespace;
    }

    public LoopbackNetNamespace Namespace { get; }

    public SharedLoopbackNetNamespace AddRef()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
            Namespace.Dispose();
    }
}
