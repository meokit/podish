using System.Runtime.CompilerServices;
using System.Text;
using Fiberish.Core.VFS.TTY;
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
    private readonly Queue<(Action, FiberTask?)> _continuations = new();

    // Process Management
    private readonly Dictionary<int, Process> _processes = [];

    private readonly Queue<FiberTask> _runQueue = new();
    private readonly Dictionary<int, FiberTask> _tasks = [];
    private readonly TimerSystem _timerSystem = new();

    // Event to wake up the scheduler loop (replaces busy wait)
    private readonly AutoResetEvent _waitEvent = new(false);

    private TtyDiscipline? _tty;

    // TTY reference for checking pending input
    public TtyDiscipline? Tty
    {
        get => _tty;
        set
        {
            if (_tty != null) _tty.Device.OnInputEnqueued -= WakeUp;
            _tty = value;
            if (_tty != null) _tty.Device.OnInputEnqueued += WakeUp;
        }
    }

    public long CurrentTick => _timerSystem.CurrentTick;
    public bool Running { get; set; } = true;

    public ILoggerFactory LoggerFactory { get; set; } = new NullLoggerFactory();

    public FiberTask? CurrentTask { get; internal set; }

    public static KernelScheduler? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public void WakeUp()
    {
        _waitEvent.Set();
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
        Logger.LogDebug("[Scheduler] Schedule task TID={TID} (from {OldStatus})", task.TID, task.Status);

        task.Status = FiberTaskStatus.Ready;
        lock (_runQueue)
        {
            _runQueue.Enqueue(task);
        }

        WakeUp();
    }

    public void Schedule(Action continuation, FiberTask? context = null)
    {
        lock (_continuations)
        {
            _continuations.Enqueue((continuation, context));
        }

        WakeUp();
    }

    public Timer ScheduleTimer(long delayTicks, Action callback)
    {
        var timer = _timerSystem.Schedule(delayTicks, callback);
        // If this timer is the new earliest one, or if we were sleeping, we should wake up
        // to re-evaluate our wait time.
        WakeUp();
        return timer;
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

                // 0. Process Continuations (High Priority)
                while (true)
                {
                    (Action, FiberTask?) item;
                    lock (_continuations)
                    {
                        if (!_continuations.TryDequeue(out item)) break;
                    }

                    var (cont, ctx) = item;
                    var oldTask = CurrentTask;
                    CurrentTask = ctx;
                    try
                    {
                        cont();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error in async continuation");
                    }
                    finally
                    {
                        CurrentTask = oldTask;
                    }
                }

                // 1. Process Timers
                // If the queue is empty, we MUST advance time to the next timer event
                // to avoid busy loop deadlocks if all tasks are waiting on timers.
                if (_runQueue.Count == 0 && _continuations.Count == 0)
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
                        // No tasks and no timers? Check if we should wait for external input.
                        if (Running)
                        {
                            // Check if TTY has pending input - if so, wake up waiting tasks
                            if (Tty != null && Tty.HasPendingInput)
                            {
                                // Process the input, which will trigger AsyncWaitQueues and schedule continuations
                                Logger.LogDebug("TTY has pending input, processing...");
                                Tty.ProcessPendingInput();
                                continue;
                            }

                            // Check scheduler state - this will detect actual bugs
                            ValidateSchedulerState();

                            // Check if any task is actually alive/waiting
                            var anyWaiting = false;
                            var anyAlive = false;
                            lock (_tasks)
                            {
                                foreach (var t in _tasks.Values)
                                    if (t.Status != FiberTaskStatus.Terminated && t.Status != FiberTaskStatus.Zombie)
                                    {
                                        anyAlive = true;
                                        if (t.Status == FiberTaskStatus.Waiting) anyWaiting = true;
                                    }
                            }

                            if (!anyAlive)
                            {
                                Logger.LogInformation("No active tasks. Exiting loop.");
                                Running = false;
                                break;
                            }

                            // If we have a TTY and tasks are waiting for input, wait a bit
                            // This allows the background InputLoop to provide input
                            // Optimized: Wait for signal instead of sleeping
                            if (Tty != null && anyWaiting)
                            {
                                Logger.LogDebug("Waiting for external input (blocking)...");
                                _waitEvent.WaitOne();
                                continue;
                            }

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

    /// <summary>
    ///     Validate scheduler state and detect scheduler bugs vs normal I/O waits.
    ///     This simplified version distinguishes between:
    ///     - Scheduler Bug: Task is Ready/Running but not being executed
    ///     - Normal I/O Wait: Task is Waiting (for external input like terminal, network, timer)
    /// </summary>
    private void ValidateSchedulerState()
    {
        lock (_tasks)
        {
            foreach (var task in _tasks.Values)
                switch (task.Status)
                {
                    case FiberTaskStatus.Ready:
                        // Ready task should be in RunQueue
                        // Note: We can't easily check if task is in queue without iterating
                        // For now, if we reach here with RunQueue empty and Ready tasks, it's a bug
                        Panic($"Scheduler Bug: Task TID={task.TID} PID={task.PID} is Ready but RunQueue is empty. " +
                              $"Task should be scheduled for execution.");
                        break;

                    case FiberTaskStatus.Running:
                        // Running task should be the current task
                        if (CurrentTask != task)
                            Panic($"Scheduler Bug: Task TID={task.TID} PID={task.PID} is Running but not Current. " +
                                  $"CurrentTask is {(CurrentTask != null ? $"TID={CurrentTask.TID}" : "null")}");
                        break;

                    case FiberTaskStatus.Waiting:
                        // Waiting is normal - task is waiting for I/O (terminal, timer, futex, etc.)
                        // This is NOT a scheduler bug, just normal blocking
                        Logger.LogDebug("Task TID={TID} is Waiting (normal I/O block)", task.TID);
                        break;

                    case FiberTaskStatus.Zombie:
                    case FiberTaskStatus.Terminated:
                        // These are fine, just need cleanup
                        break;
                }
        }
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