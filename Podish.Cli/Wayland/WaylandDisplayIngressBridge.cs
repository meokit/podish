using System.Collections.Concurrent;

namespace Podish.Cli.Wayland;

internal sealed class WaylandDisplayIngressBridge
{
    private readonly ConcurrentQueue<WaylandDisplayIngressEvent> _pending = [];
    private Action<WaylandDisplayIngressEvent>? _sink;

    public void Bind(Action<WaylandDisplayIngressEvent> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        while (_pending.TryDequeue(out WaylandDisplayIngressEvent ingress))
            _sink(ingress);
    }

    public void Post(WaylandDisplayIngressEvent ingress)
    {
        Action<WaylandDisplayIngressEvent>? sink = _sink;
        if (sink != null)
        {
            sink(ingress);
            return;
        }

        _pending.Enqueue(ingress);
        sink = _sink;
        if (sink == null)
            return;

        while (_pending.TryDequeue(out ingress))
            sink(ingress);
    }
}
