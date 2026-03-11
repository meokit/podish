using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

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
///     A hierarchical time wheel implementation optimized for fast insertion and tick processing.
///     Tick size is assumed to be 1 millisecond.
/// </summary>
public class TimeWheel
{
    private const int TVR_BITS = 8;
    private const int TVN_BITS = 6;
    private const int TVR_SIZE = 1 << TVR_BITS;
    private const int TVN_SIZE = 1 << TVN_BITS;
    private const int TVR_MASK = TVR_SIZE - 1;
    private const int TVN_MASK = TVN_SIZE - 1;
    private static readonly ILogger Logger = Logging.CreateLogger<TimeWheel>();

    private readonly Timer?[] _tv1 = new Timer?[TVR_SIZE];
    private readonly Timer?[] _tv2 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv3 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv4 = new Timer?[TVN_SIZE];
    private readonly Timer?[] _tv5 = new Timer?[TVN_SIZE];
    private int _count;

    public TimeWheel(long startTick = 0)
    {
        CurrentTick = startTick;
    }

    public long CurrentTick { get; private set; } // Milliseconds

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
                    Logger.LogTrace(
                        "[TimeWheel] Fire timer currentTick={CurrentTick} expiration={Expiration} callback={Callback}",
                        CurrentTick, timer.ExpirationTick, timer.Callback?.Method.Name ?? "<null>");
                    timer.Callback?.Invoke();
                }

                _count--;
                timer = next;
            }

            CurrentTick++;
        }
    }

    public long? GetNextExpiration()
    {
        if (_count == 0) return null;

        // Check TV1
        var currentIdx = (int)(CurrentTick & TVR_MASK);
        for (var i = 0; i < TVR_SIZE; i++)
        {
            var idx = (currentIdx + i) & TVR_MASK;
            if (_tv1[idx] != null)
            {
                var min = GetMinExpiration(_tv1[idx]);
                if (min.HasValue) return min;
            }
        }

        // Check TV2 .. TV5
        long? ret;
        if ((ret = CheckCascade(_tv2, 0, TVR_BITS)) != null) return ret;
        if ((ret = CheckCascade(_tv3, 1, TVR_BITS + TVN_BITS)) != null) return ret;
        if ((ret = CheckCascade(_tv4, 2, TVR_BITS + 2 * TVN_BITS)) != null) return ret;
        if ((ret = CheckCascade(_tv5, 3, TVR_BITS + 3 * TVN_BITS)) != null) return ret;

        return null;
    }

    private long? CheckCascade(Timer?[] vec, int levelIndex, int shift)
    {
        var currentIdx = (int)((CurrentTick >> shift) & TVN_MASK);
        for (var i = 1; i < TVN_SIZE; i++)
        {
            var idx = (currentIdx + i) & TVN_MASK;
            if (vec[idx] != null)
            {
                var min = GetMinExpiration(vec[idx]);
                if (min.HasValue) return min;
            }
        }

        return null;
    }

    private long? GetMinExpiration(Timer? head)
    {
        var min = long.MaxValue;
        var t = head;
        var found = false;
        while (t != null)
        {
            if (!t.Canceled)
            {
                if (t.ExpirationTick < min) min = t.ExpirationTick;
                found = true;
            }

            t = t.Next;
        }

        return found ? min : null;
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