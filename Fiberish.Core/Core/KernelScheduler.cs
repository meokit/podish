using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Fiberish.Memory;
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
    private int _nextTaskId;
    private int _initPid;
    private int _engineInitReaperEnabled;

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
    public int InitPid => Volatile.Read(ref _initPid);
    public bool EngineInitReaperEnabled => Volatile.Read(ref _engineInitReaperEnabled) != 0;

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

    public void CleanupDeadProcess(Process process)
    {
        TryReleaseProcessMemory(process, process.Syscalls.Engine);
        DetachProcessTasks(process.TGID);
    }

    public bool TryReleaseProcessMemory(Process process, Engine engine)
    {
        if (process.MemoryReleased) return true;

        var beforeBytes = ExternalPageManager.GetAllocatedBytes();
        var beforeByClass = ExternalPageManager.GetAllocationClassStatsSummary();
        var (cowFirstBefore, cowReplaceBefore) = VMAManager.GetCowAllocationCounters();
        var refsBefore = process.Mem.GetSharedRefCount();
        var refsAfter = process.Mem.ReleaseSharedRef(engine);
        process.MemoryReleased = true;
        var afterBytes = ExternalPageManager.GetAllocatedBytes();
        var afterByClass = ExternalPageManager.GetAllocationClassStatsSummary();
        var (cowFirstAfter, cowReplaceAfter) = VMAManager.GetCowAllocationCounters();
        Logger.LogDebug(
            "[MemRelease] PID={Pid} released VM pages bytesBefore={BeforeBytes} bytesAfter={AfterBytes} " +
            "classesBefore=[{BeforeByClass}] classesAfter=[{AfterByClass}] " +
            "refsBefore={RefsBefore} refsAfter={RefsAfter} " +
            "cowCountersBefore=first:{CowFirstBefore},replace:{CowReplaceBefore} " +
            "cowCountersAfter=first:{CowFirstAfter},replace:{CowReplaceAfter}",
            process.TGID, beforeBytes, afterBytes, beforeByClass, afterByClass,
            refsBefore, refsAfter,
            cowFirstBefore, cowReplaceBefore, cowFirstAfter, cowReplaceAfter);
        return true;
    }

    public void DetachProcessTasks(int pid)
    {
        List<FiberTask> tasksToRemove;
        lock (_tasks)
        {
            tasksToRemove = _tasks.Values.Where(t => t.PID == pid).ToList();
            foreach (var task in tasksToRemove) _tasks.Remove(task.TID);
        }

        foreach (var task in tasksToRemove)
        {
            task.Status = FiberTaskStatus.Terminated;
            lock (task.Process.Threads)
            {
                task.Process.Threads.Remove(task);
            }
        }
    }

    public FiberTask? GetTask(int tid)
    {
        lock (_tasks)
        {
            return _tasks.TryGetValue(tid, out var t) ? t : null;
        }
    }

    public int AllocateTaskId()
    {
        return Interlocked.Increment(ref _nextTaskId);
    }

    public void SetInitPid(int pid)
    {
        if (pid <= 0) return;
        Interlocked.CompareExchange(ref _initPid, pid, 0);
    }

    public void SetEngineInitReaperEnabled(bool enabled)
    {
        Volatile.Write(ref _engineInitReaperEnabled, enabled ? 1 : 0);
    }

    public int ReparentChildrenToInit(int exitingPid)
    {
        var initPid = InitPid;
        if (initPid <= 0 || exitingPid <= 0 || exitingPid == initPid) return 0;
        return ReparentChildren(exitingPid, initPid);
    }

    public int ReparentChildren(int fromPid, int toPid)
    {
        if (fromPid <= 0 || toPid <= 0 || fromPid == toPid) return 0;

        Process? fromProc;
        Process? toProc;
        List<int> adoptedPids = [];

        lock (_processes)
        {
            _processes.TryGetValue(fromPid, out fromProc);
            if (!_processes.TryGetValue(toPid, out toProc)) return 0;

            foreach (var proc in _processes.Values)
            {
                if (proc.PPID != fromPid) continue;
                proc.PPID = toPid;
                adoptedPids.Add(proc.TGID);
            }
        }

        if (adoptedPids.Count == 0) return 0;

        if (fromProc != null)
        {
            lock (fromProc.Children)
            {
                foreach (var pid in adoptedPids) fromProc.Children.Remove(pid);
            }
        }

        lock (toProc!.Children)
        {
            foreach (var pid in adoptedPids)
            {
                if (!toProc.Children.Contains(pid)) toProc.Children.Add(pid);
            }
        }

        var initTask = GetTask(toPid);
        if (initTask != null)
        {
            initTask.PostSignal((int)Signal.SIGCHLD);
            initTask.TrySetActiveWaitReason(WakeReason.Event);
            Schedule(initTask);
        }

        return adoptedPids.Count;
    }

    public bool TryAutoReapZombie(Process process)
    {
        if (!EngineInitReaperEnabled) return false;
        if (process.State != ProcessState.Zombie) return false;
        if (process.TGID == InitPid || process.PPID != InitPid) return false;

        Process? initProc;
        lock (_processes)
        {
            if (!_processes.TryGetValue(process.TGID, out var live) || !ReferenceEquals(live, process)) return false;
            if (!_processes.TryGetValue(InitPid, out initProc)) return false;
            _processes.Remove(process.TGID);
        }

        lock (initProc!.Children)
        {
            initProc.Children.Remove(process.TGID);
        }

        process.State = ProcessState.Dead;
        CleanupDeadProcess(process);
        return true;
    }

    public void Schedule(FiberTask task)
    {
        if (task.Status == FiberTaskStatus.Terminated) return;
        // Logger.LogDebug("[Scheduler] Schedule task TID={TID} (from {OldStatus})", task.TID, task.Status);

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
        var beforeTick = _timerSystem.CurrentTick;
        var targetTick = _timerSystem.CurrentTick + delayMs;
        // Adjust for any lag between CurrentTick and real time
        var realNow = _sw.ElapsedMilliseconds;
        if (targetTick < realNow + delayMs) targetTick = realNow + delayMs;

        Logger.LogTrace(
            "[Scheduler] ScheduleTimer delayMs={DelayMs} currentTick={CurrentTick} realNow={RealNow} targetTick={TargetTick}",
            delayMs, beforeTick, realNow, targetTick);
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
        var previousSyncContext = SynchronizationContext.Current;
        var schedulerSyncContext = new KernelSyncContext(this);
        SynchronizationContext.SetSynchronizationContext(schedulerSyncContext);
        Logger.LogInformation("KernelScheduler started.");
        var exitReason = "running=false";

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
                    exitReason = "max-ticks";
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
                        exitReason = "no-active-tasks";
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
                    var previous = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(schedulerSyncContext.WithTaskContext(task));
                    try
                    {
                        task.RunSlice();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                    }

                    // Time accounting (simplified)
                    // _timerSystem.Advance(1); // Removed: Time is driven by _sw.ElapsedMilliseconds
                }
            }
        }
        catch (Exception ex)
        {
            exitReason = $"exception:{ex.GetType().Name}";
            Logger.LogError(ex, "KernelScheduler crashed.");
            throw;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSyncContext);
            Logger.LogInformation("KernelScheduler stopped. reason={Reason} running={Running} tick={Tick}", exitReason,
                Running, CurrentTick);
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
        var previousSyncContext = SynchronizationContext.Current;
        if (KernelScheduler.Current == this)
            SynchronizationContext.SetSynchronizationContext(new KernelSyncContext(this, ctx));
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
            SynchronizationContext.SetSynchronizationContext(previousSyncContext);
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
        if (mainTask != null)
        {
            mainTask.PostSignal(signal);
            return true;
        }

        // Engine-managed init has no FiberTask. In --init mode, forward init's signals
        // to its direct children so kill(1, sig) semantics remain usable.
        if (EngineInitReaperEnabled && pid == InitPid)
        {
            return ForwardSignalFromEngineInit(signal) > 0;
        }

        return false;
    }

    public bool SignalProcessInfo(int pid, int signal, SigInfo info)
    {
        Process? proc;
        lock (_processes)
        {
            if (!_processes.TryGetValue(pid, out proc)) return false;
        }

        var mainTask = GetTask(proc.TGID);
        if (mainTask != null)
        {
            mainTask.PostSignalInfo(info);
            return true;
        }

        if (EngineInitReaperEnabled && pid == InitPid)
        {
            return ForwardSignalFromEngineInit(signal) > 0;
        }

        return false;
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

    private int ForwardSignalFromEngineInit(int signal)
    {
        Process? initProc;
        lock (_processes)
        {
            if (!_processes.TryGetValue(InitPid, out initProc)) return 0;
        }

        List<int> childPids;
        lock (initProc!.Children)
        {
            childPids = [.. initProc.Children];
        }

        var count = 0;
        foreach (var childPid in childPids)
        {
            var child = GetProcess(childPid);
            if (child == null) continue;
            if (child.State is ProcessState.Zombie or ProcessState.Dead) continue;
            if (SignalProcess(childPid, signal)) count++;
        }

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
