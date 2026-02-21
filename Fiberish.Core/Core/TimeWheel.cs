namespace Fiberish.Core;

public class Timer
{
    public long ExpirationTick { get; set; }
    public Action? Callback { get; set; }
    public bool Canceled { get; set; }
    public Timer? Next { get; set; }
    public Timer? Prev { get; set; }

    public void Cancel()
    {
        Canceled = true;
    }
}

/// <summary>
/// A hierarchical time wheel implementation optimized for fast insertion and tick processing.
/// Tick size is assumed to be 1 millisecond.
/// </summary>
public class TimeWheel
{
    private const int TVR_BITS = 8;
    private const int TVN_BITS = 6;
    private const int TVR_SIZE = 1 << TVR_BITS;
    private const int TVN_SIZE = 1 << TVN_BITS;
    private const int TVR_MASK = TVR_SIZE - 1;
    private const int TVN_MASK = TVN_SIZE - 1;

    private readonly Timer?[] _tv1 = new Timer?[TVR_SIZE];
    private readonly Timer?[] _tv2 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv3 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv4 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv5 = new Timer?[TVN_SIZE];

    public long CurrentTick { get; private set; } // Milliseconds
    private int _count = 0;

    public TimeWheel(long startTick = 0)
    {
        CurrentTick = startTick;
    }

    public bool HasTimers => _count > 0;

    public Timer Schedule(long delayTicks, Action callback)
    {
        return ScheduleAbsolute(CurrentTick + delayTicks, callback);
    }

    public Timer ScheduleAbsolute(long targetTick, Action callback)
    {
        var timer = new Timer { ExpirationTick = targetTick, Callback = callback };
        AddTimer(timer);
        _count++;
        return timer;
    }

    private void AddTimer(Timer timer)
    {
        var expires = timer.ExpirationTick;
        var idx = expires - CurrentTick;
        Timer?[] vec;
        int slot;

        if (idx < 0)
        {
            vec = _tv1;
            slot = (int)(CurrentTick & TVR_MASK);
        }
        else if (idx < TVR_SIZE)
        {
            vec = _tv1;
            slot = (int)(expires & TVR_MASK);
        }
        else if (idx < 1 << (TVR_BITS + TVN_BITS))
        {
            vec = _tv2;
            slot = (int)((expires >> TVR_BITS) & TVN_MASK);
        }
        else if (idx < 1 << (TVR_BITS + 2 * TVN_BITS))
        {
            vec = _tv3;
            slot = (int)((expires >> (TVR_BITS + TVN_BITS)) & TVN_MASK);
        }
        else if (idx < 1 << (TVR_BITS + 3 * TVN_BITS))
        {
            vec = _tv4;
            slot = (int)((expires >> (TVR_BITS + 2 * TVN_BITS)) & TVN_MASK);
        }
        else
        {
            vec = _tv5;
            slot = (int)((expires >> (TVR_BITS + 3 * TVN_BITS)) & TVN_MASK);
        }

        var head = vec[slot];
        timer.Next = head;
        timer.Prev = null;
        if (head != null) head.Prev = timer;
        vec[slot] = timer;
    }

    public void Advance(long targetTick)
    {
        while (CurrentTick < targetTick)
        {
            var index = (int)(CurrentTick & TVR_MASK);
            if (index == 0 && CurrentTick != 0) CascadeIfNecessary();

            var timer = _tv1[index];
            _tv1[index] = null;

            while (timer != null)
            {
                var next = timer.Next;
                if (!timer.Canceled)
                {
                    timer.Callback?.Invoke();
                }
                _count--;
                timer = next;
            }

            CurrentTick++;
        }
    }

    private void Cascade(Timer?[] vec, int index)
    {
        var timer = vec[index];
        vec[index] = null;
        while (timer != null)
        {
            var next = timer.Next;
            AddTimer(timer);
            timer = next;
        }
    }

    private void CascadeIfNecessary()
    {
        var tick = CurrentTick;
        var idx1 = (int)((tick >> TVR_BITS) & TVN_MASK);
        Cascade(_tv2, idx1);
        if (idx1 != 0) return;

        var idx2 = (int)((tick >> (TVR_BITS + TVN_BITS)) & TVN_MASK);
        Cascade(_tv3, idx2);
        if (idx2 != 0) return;

        var idx3 = (int)((tick >> (TVR_BITS + 2 * TVN_BITS)) & TVN_MASK);
        Cascade(_tv4, idx3);
        if (idx3 != 0) return;

        var idx4 = (int)((tick >> (TVR_BITS + 3 * TVN_BITS)) & TVN_MASK);
        Cascade(_tv5, idx4);
    }
}
