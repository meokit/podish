namespace Fiberish.Core;

public class Timer(long point, Action callback)
{
    public long ExpirationTick { get; set; } = point;
    public Action Callback { get; set; } = callback;
    public bool Canceled { get; set; }

    public void Cancel()
    {
        Canceled = true;
    }
}

public class TimerSystem
{
    private readonly PriorityQueue<Timer, long> _queue = new();

    public long CurrentTick { get; private set; }

    public long? NextExpiration => _queue.Count > 0 ? _queue.Peek().ExpirationTick : null;

    // Constants: 1 Tick = 1 microsecond? Or 1ms? 
    // Let's assume 1 Tick = 1 microsecond for high precision simulation.
    // 1ms = 1000 ticks.

    public void Advance(long ticks)
    {
        CurrentTick += ticks;
        ProcessExpired();
    }

    public Timer Schedule(long delayTicks, Action callback)
    {
        var timer = new Timer(CurrentTick + delayTicks, callback);
        _queue.Enqueue(timer, timer.ExpirationTick);
        return timer;
    }

    public Timer ScheduleAbsolute(long targetTick, Action callback)
    {
        var timer = new Timer(targetTick, callback);
        _queue.Enqueue(timer, timer.ExpirationTick);
        return timer;
    }

    private void ProcessExpired()
    {
        while (_queue.Count > 0)
        {
            if (_queue.Peek().ExpirationTick > CurrentTick)
                break;

            var timer = _queue.Dequeue();
            if (!timer.Canceled) timer.Callback?.Invoke();
        }
    }
}