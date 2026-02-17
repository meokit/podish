using System.Runtime.CompilerServices;
using System.Text;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiberish.Core;

public class KernelScheduler
{
    private static readonly ILogger Logger = Logging.CreateLogger<KernelScheduler>();

    // Global instance (or dependency injected)
    // public static KernelScheduler Instance { get; set; } = new();

    private static readonly AsyncLocal<KernelScheduler?> _current = new();

    // Process Management
    private readonly Dictionary<int, Process> _processes = [];

    private readonly Queue<FiberTask> _runQueue = new();
    private readonly Dictionary<int, FiberTask> _tasks = [];
    private readonly TimerSystem _timerSystem = new();

    public long CurrentTick => _timerSystem.CurrentTick;
    public bool Running { get; set; } = true;

    public ILoggerFactory LoggerFactory { get; set; } = new NullLoggerFactory();

    public FiberTask? CurrentTask { get; internal set; }

    public static KernelScheduler? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public void RegisterProcess(Process p)
    {
        lock (_processes)
        {
            _processes[p.TGID] = p;
        }
    }

    public void RegisterTask(FiberTask t)
    {
        lock (_tasks)
        {
            _tasks[t.TID] = t;
        }

        Schedule(t);
    }

    public Process? GetProcess(int pid)
    {
        lock (_processes)
        {
            return _processes.TryGetValue(pid, out var p) ? p : null;
        }
    }

    public FiberTask? GetTask(int tid)
    {
        lock (_tasks)
        {
            return _tasks.TryGetValue(tid, out var t) ? t : null;
        }
    }

    public void Schedule(FiberTask task)
    {
        if (task.Status == FiberTaskStatus.Terminated) return;

        task.Status = FiberTaskStatus.Ready;
        lock (_runQueue)
        {
            _runQueue.Enqueue(task);
        }
    }

    public Timer ScheduleTimer(long delayTicks, Action callback)
    {
        return _timerSystem.Schedule(delayTicks, callback);
    }

    public static TimerAwaiter Sleep(long ticks)
    {
        return new TimerAwaiter(ticks);
    }

    public void Run(long maxTicks = -1)
    {
        Current = this;
        Logger.LogInformation("KernelScheduler started.");

        try
        {
            while (Running)
            {
                if (maxTicks > 0 && CurrentTick >= maxTicks)
                {
                    Logger.LogWarning("KernelScheduler reached max ticks limit. Stopping.");
                    break;
                }

                // 1. Process Timers
                // If the queue is empty, we MUST advance time to the next timer event
                // to avoid busy loop deadlocks if all tasks are waiting on timers.
                if (_runQueue.Count == 0)
                {
                    var nextWakeup = _timerSystem.NextExpiration;
                    if (nextWakeup.HasValue)
                    {
                        var jump = nextWakeup.Value - CurrentTick;
                        if (jump > 0)
                            _timerSystem.Advance(jump);
                        else
                            _timerSystem.Advance(0);
                    }
                    else
                    {
                        // No tasks and no timers? We are done or deadlocked.
                        if (Running)
                        {
                            // Check if we have active tasks stuck
                            var activeCount = 0;
                            lock (_tasks)
                            {
                                foreach (var t in _tasks.Values)
                                    if (t.Status != FiberTaskStatus.Terminated && t.Status != FiberTaskStatus.Zombie)
                                        activeCount++;
                            }

                            if (activeCount > 0)
                                Panic(
                                    $"Deadlock detected? RunQueue empty, No Timers, but {activeCount} tasks are still active (Waiting?).");

                            Logger.LogInformation("No ready tasks and no pending timers. Exiting loop.");
                            Running = false;
                            break;
                        }
                    }
                }
                else
                {
                    // Normal execution: Advance time by small slice?
                    // For determinism, maybe we advance time by X ticks per instruction executed?
                    // For now, let's just process timers at current tick.
                    _timerSystem.Advance(0);
                }

                // 2. Run Task
                if (TryDequeue(out var task) && task != null)
                {
                    task.Status = FiberTaskStatus.Running;

                    // Execute a slice
                    // We advance time based on how much work the task did
                    task.RunSlice();

                    // Time accounting (simplified)
                    _timerSystem.Advance(1);
                }
            }
        }
        finally
        {
            Current = null;
        }
    }

    public void Panic(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KERNEL PANIC: {reason}");
        sb.AppendLine($"CurrentTick: {CurrentTick}");
        sb.AppendLine($"RunQueue: {_runQueue.Count}");

        lock (_tasks)
        {
            sb.AppendLine("Tasks:");
            foreach (var t in _tasks.Values)
                sb.AppendLine(
                    $"  TID={t.TID} PID={t.PID} Status={t.Status} Exited={t.Exited} ExitStatus={t.ExitStatus}");
        }

        var msg = sb.ToString();
        Logger.LogCritical(msg);
        Console.Error.WriteLine(msg); // Ensure we see it in test output
        throw new KernelPanicException(msg);
    }

    private bool TryDequeue(out FiberTask? task)
    {
        lock (_runQueue)
        {
            return _runQueue.TryDequeue(out task!);
        }
    }

    public void SignalProcessGroup(int pgid, int signal)
    {
        lock (_processes)
        {
            foreach (var p in _processes.Values)
            {
                if (p.PGID == pgid)
                {
                    // Signal all threads? Usually signal is delivered to process, 
                    // and one thread handles it. But for job control (SIGINT/SIGTSTP), 
                    // it affects the whole group.
                    // In Linux, it's sent to all processes in group. 
                    // For each process, we signal its main thread or all threads?
                    // Typically signal pending on process, handled by any eligible thread.
                    // Simplified: Signal the main thread (TID=TGID).
                    
                    var mainTask = GetTask(p.TGID);
                    mainTask?.HandleSignal(signal);
                }
            }
        }
    }

    public void SignalTask(int tid, int signal)
    {
        var task = GetTask(tid);
        task?.HandleSignal(signal);
    }

    public readonly struct TimerAwaiter(long ticks) : INotifyCompletion
    {
        private readonly long _ticks = ticks;

        public bool IsCompleted => _ticks <= 0;

        public void OnCompleted(Action continuation)
        {
            Current!.ScheduleTimer(_ticks, continuation);
        }

        public void GetResult()
        {
        }
    }

    public class KernelPanicException : Exception
    {
        public KernelPanicException(string message) : base(message)
        {
        }
    }
}