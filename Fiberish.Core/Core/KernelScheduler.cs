using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiberish.Core;

public class KernelScheduler
{
    private static readonly ILogger Logger = Logging.CreateLogger<KernelScheduler>();

    // Global instance (or dependency injected)
    // public static KernelScheduler Instance { get; set; } = new();

    private static readonly AsyncLocal<KernelScheduler?> _current = new();

    private readonly Channel<(Action, FiberTask?)> _events =
        Channel.CreateUnbounded<(Action, FiberTask?)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // Process Management
    private readonly Dictionary<int, Process> _processes = [];

    private readonly Queue<FiberTask> _runQueue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Dictionary<int, FiberTask> _tasks = [];
    private readonly TimeWheel _timerSystem = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);

    private TtyDiscipline? _tty;
    private int _wakePending;

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
        EnqueueWake();
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

    public List<Process> GetProcessesSnapshot()
    {
        lock (_processes)
        {
            return _processes.Values.ToList();
        }
    }

    public bool UnregisterProcess(int pid)
    {
        lock (_processes)
        {
            return _processes.Remove(pid);
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

        EnqueueWake();
    }

    public void Schedule(Action continuation, FiberTask? context = null)
    {
        _events.Writer.TryWrite((continuation, context));
        EnqueueWake();
    }

    public Timer ScheduleTimer(long delayMs, Action callback)
    {
        var targetTick = _timerSystem.CurrentTick + delayMs;
        // Adjust for any lag between CurrentTick and real time
        var realNow = _sw.ElapsedMilliseconds;
        if (targetTick < realNow + delayMs) targetTick = realNow + delayMs;

        var timer = _timerSystem.ScheduleAbsolute(targetTick, callback);
        // If this timer is the new earliest one, or if we were sleeping, we should wake up
        // to re-evaluate our wait time.
        EnqueueWake();
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

        var startTick = _timerSystem.CurrentTick;

        try
        {
            while (Running)
            {
                // Advance timer to current physical time (in ms)
                var nowMs = startTick + _sw.ElapsedMilliseconds;
                _timerSystem.Advance(nowMs);

                if (maxTicks > 0 && CurrentTick >= maxTicks)
                {
                    Logger.LogWarning("KernelScheduler reached max ticks limit. Stopping.");
                    break;
                }

                // 0. Process Continuations (High Priority)
                var drainedEvents = DrainEvents();

                // 1. Process Timers & Wait
                if (_runQueue.Count == 0 && !drainedEvents)
                {
                    if (TryProcessPendingInput())
                        continue;

                    // Check scheduler state - this will detect actual bugs
                    ValidateSchedulerState();

                    var (anyAlive, anyWaiting) = GetTaskLiveness();
                    if (!anyAlive)
                    {
                        Logger.LogInformation("No active tasks. Exiting loop.");
                        Running = false;
                        break;
                    }

                    // Calculate wait time
                    long waitTime = -1;
                    if (_timerSystem.HasTimers)
                    {
                        var nextTick = _timerSystem.GetNextExpiration();
                        if (nextTick.HasValue)
                        {
                            waitTime = nextTick.Value - _timerSystem.CurrentTick;
                            if (waitTime < 0) waitTime = 0;
                        }
                    }
                    else if (Tty != null && anyWaiting)
                    {
                        waitTime = -1; // Wait indefinitely for TTY or external events
                    }
                    else
                    {
                        // No timers, no tasks waiting on TTY?
                        // If anyWaiting is true, we must wait indefinitely (e.g. waiting on a signal or socket)
                        if (anyWaiting) waitTime = -1;
                        else
                            // Should not happen if anyAlive is true but runQueue empty (ValidateSchedulerState handles Ready tasks)
                            // Maybe just yield?
                            waitTime = 0;
                    }

                    // If waitTime is 0 (timer due now), we must wait at least 1ms to let wall clock advance
                    // otherwise we busy loop until _sw.ElapsedMilliseconds increases.
                    if (waitTime == 0) waitTime = 1;

                    WaitForEvent((int)waitTime);
                    continue;
                }

                // 2. Run Task
                if (TryDequeue(out var task) && task != null)
                {
                    // Stale queue entry: task may have transitioned to Waiting/Terminated
                    // after it was enqueued (e.g., async syscall path). Skip safely.
                    if (task.Status != FiberTaskStatus.Ready)
                        continue;

                    task.Status = FiberTaskStatus.Running;

                    // Execute a slice
                    // We advance time based on how much work the task did
                    task.RunSlice();

                    // Time accounting (simplified)
                    // _timerSystem.Advance(1); // Removed: Time is driven by _sw.ElapsedMilliseconds
                }
            }
        }
        finally
        {
            Current = null;
        }
    }

    private void DrainContinuations()
    {
        DrainEvents();
    }

    private bool DrainEvents()
    {
        var drained = false;
        while (_events.Reader.TryRead(out var item))
        {
            drained = true;
            ExecuteEvent(item);
        }

        if (drained) _wakeEvent.Reset();
        return drained;
    }

    private void WaitForEvent()
    {
        WaitForEvent(-1);
    }

    private void WaitForEvent(int timeoutMs)
    {
        _wakeEvent.Wait(timeoutMs);
        _wakeEvent.Reset();
    }

    private void ExecuteEvent((Action, FiberTask?) item)
    {
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

    private void EnqueueWake()
    {
        // Always signal event
        _wakeEvent.Set();

        if (Interlocked.Exchange(ref _wakePending, 1) != 0) return;
        _events.Writer.TryWrite((() => { _wakePending = 0; }, null));
    }

    private bool TryProcessPendingInput()
    {
        if (Tty == null || !Tty.HasPendingInput) return false;

        Logger.LogDebug("TTY has pending input, processing...");
        Tty.ProcessPendingInput();
        return true;
    }

    private (bool anyAlive, bool anyWaiting) GetTaskLiveness()
    {
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

        return (anyAlive, anyWaiting);
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
                        // Logger.LogDebug("Task TID={TID} is Waiting (normal I/O block)", task.TID);
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
        _ = SignalProcessGroupWithCount(pgid, signal);
    }

    public int SignalProcessGroupWithCount(int pgid, int signal)
    {
        var count = 0;
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
                    if (mainTask == null) continue;
                    mainTask.PostSignal(signal);
                    count++;
                }
        }

        return count;
    }

    public bool SignalProcess(int pid, int signal)
    {
        Process? proc;
        lock (_processes)
        {
            if (!_processes.TryGetValue(pid, out proc)) return false;
        }

        var mainTask = GetTask(proc.TGID);
        if (mainTask == null) return false;
        mainTask.PostSignal(signal);
        return true;
    }

    public bool SignalProcessInfo(int pid, int signal, SigInfo info)
    {
        Process? proc;
        lock (_processes)
        {
            if (!_processes.TryGetValue(pid, out proc)) return false;
        }

        var mainTask = GetTask(proc.TGID);
        if (mainTask == null) return false;
        mainTask.PostSignalInfo(info);
        return true;
    }

    public int SignalAllProcesses(int signal, int? excludePid = null, bool skipInit = true)
    {
        List<int> pids = [];
        lock (_processes)
        {
            foreach (var p in _processes.Values)
            {
                if (excludePid.HasValue && p.TGID == excludePid.Value) continue;
                if (skipInit && p.TGID == 1) continue;
                pids.Add(p.TGID);
            }
        }

        var count = 0;
        foreach (var pid in pids)
            if (SignalProcess(pid, signal))
                count++;

        return count;
    }

    public void SignalTask(int tid, int signal)
    {
        var task = GetTask(tid);
        task?.PostSignal(signal);
    }

    public bool IsValidProcessGroup(int pgid, int sid)
    {
        lock (_processes)
        {
            foreach (var p in _processes.Values)
                if (p.PGID == pgid && p.SID == sid)
                    return true;
        }

        return false;
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
