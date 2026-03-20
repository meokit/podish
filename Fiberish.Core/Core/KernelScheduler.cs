using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiberish.Core;

public class KernelScheduler
{
    private static readonly ILogger Logger = Logging.CreateLogger<KernelScheduler>();

    // Global instance (or dependency injected)
    // public static KernelScheduler Instance { get; set; } = new();


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
    private int _engineInitReaperEnabled;
    private int _initPid;
    private int _nextTaskId;
    private int _ownerThreadId;

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


    public int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    public bool IsSchedulerThread
    {
        get
        {
            var owner = OwnerThreadId;
            return owner == 0 || owner == Environment.CurrentManagedThreadId;
        }
    }

    private void BindOwnerThreadIfNeeded()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var previous = Interlocked.CompareExchange(ref _ownerThreadId, currentThreadId, 0);
        if (previous != 0 && previous != currentThreadId)
            throw new InvalidOperationException(
                $"KernelScheduler is already bound to thread {previous}, current thread is {currentThreadId}.");
    }

    public void AssertSchedulerThread([CallerMemberName] string? caller = null)
    {
        var owner = OwnerThreadId;
        if (owner == 0) return;
        if (owner == Environment.CurrentManagedThreadId) return;
        throw new InvalidOperationException(
            $"KernelScheduler.{caller ?? "<unknown>"} must run on scheduler thread {owner}, current thread is {Environment.CurrentManagedThreadId}. Use ScheduleFromAnyThread.");
    }

    public void WakeUp()
    {
        EnqueueWake();
    }

    public void RegisterProcess(Process p)
    {
        AssertSchedulerThread();
        _processes[p.TGID] = p;
    }

    public void RegisterTask(FiberTask t)
    {
        AssertSchedulerThread();
        _tasks[t.TID] = t;

        ScheduleLocal(t);
    }

    public Process? GetProcess(int pid)
    {
        AssertSchedulerThread();
        return _processes.TryGetValue(pid, out var p) ? p : null;
    }

    public List<Process> GetProcessesSnapshot()
    {
        AssertSchedulerThread();
        return _processes.Values.ToList();
    }

    public bool UnregisterProcess(int pid)
    {
        AssertSchedulerThread();
        return _processes.Remove(pid);
    }

    public void CleanupDeadProcess(Process process)
    {
        AssertSchedulerThread();
        var mem = process.Mem;
        var engine = process.Syscalls?.Engine;
        if (mem != null && engine != null)
            TryReleaseProcessMemory(process, engine);
        else
            process.MemoryReleased = true;
        DetachProcessTasks(process.TGID);
    }

    public bool TryReleaseProcessMemory(Process process, Engine engine)
    {
        AssertSchedulerThread();
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
        AssertSchedulerThread();
        var tasksToRemove = _tasks.Values.Where(t => t.PID == pid).ToList();
        foreach (var task in tasksToRemove) _tasks.Remove(task.TID);

        foreach (var task in tasksToRemove)
        {
            task.Process.Syscalls.UnregisterEngine(task.CPU);
            task.Status = FiberTaskStatus.Terminated;
            task.ExecutionMode = TaskExecutionMode.Terminated;
            task.Process.Threads.Remove(task);
        }
    }

    public int DetachTask(FiberTask task)
    {
        AssertSchedulerThread();
        _tasks.Remove(task.TID);

        task.Process.Syscalls.UnregisterEngine(task.CPU);
        task.Status = FiberTaskStatus.Terminated;
        task.ExecutionMode = TaskExecutionMode.Terminated;

        task.Process.Threads.Remove(task);
        return task.Process.Threads.Count;
    }

    public FiberTask? GetTask(int tid)
    {
        AssertSchedulerThread();
        return _tasks.TryGetValue(tid, out var t) ? t : null;
    }

    public int AllocateTaskId()
    {
        AssertSchedulerThread();
        return Interlocked.Increment(ref _nextTaskId);
    }

    public void SetInitPid(int pid)
    {
        AssertSchedulerThread();
        if (pid <= 0) return;
        Interlocked.CompareExchange(ref _initPid, pid, 0);
    }

    public void SetEngineInitReaperEnabled(bool enabled)
    {
        AssertSchedulerThread();
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
        AssertSchedulerThread();
        if (fromPid <= 0 || toPid <= 0 || fromPid == toPid) return 0;

        Process? fromProc;
        Process? toProc;
        List<int> adoptedPids = [];

        _processes.TryGetValue(fromPid, out fromProc);
        if (!_processes.TryGetValue(toPid, out toProc)) return 0;

        foreach (var proc in _processes.Values)
        {
            if (proc.PPID != fromPid) continue;
            proc.PPID = toPid;
            adoptedPids.Add(proc.TGID);
        }

        if (adoptedPids.Count == 0) return 0;

        if (fromProc != null)
            foreach (var pid in adoptedPids)
                fromProc.Children.Remove(pid);

        foreach (var pid in adoptedPids)
            if (!toProc!.Children.Contains(pid))
                toProc.Children.Add(pid);

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
        AssertSchedulerThread();
        if (!EngineInitReaperEnabled) return false;
        if (process.State != ProcessState.Zombie) return false;
        if (process.TGID == InitPid || process.PPID != InitPid) return false;

        Process? initProc;
        if (!_processes.TryGetValue(process.TGID, out var live) || !ReferenceEquals(live, process)) return false;
        if (!_processes.TryGetValue(InitPid, out initProc)) return false;
        _processes.Remove(process.TGID);

        initProc!.Children.Remove(process.TGID);

        process.State = ProcessState.Dead;
        CleanupDeadProcess(process);
        return true;
    }

    private void EnqueueTask(FiberTask task)
    {
        AssertSchedulerThread();
        if (task.Status == FiberTaskStatus.Terminated) return;
        // Logger.LogDebug("[Scheduler] Schedule task TID={TID} (from {OldStatus})", task.TID, task.Status);

        task.Status = FiberTaskStatus.Ready;
        _runQueue.Enqueue(task);

        EnqueueWake();
    }

    public void ScheduleLocal(FiberTask task)
    {
        AssertSchedulerThread();
        EnqueueTask(task);
    }

    public void ScheduleFromAnyThread(FiberTask task)
    {
        if (IsSchedulerThread)
        {
            EnqueueTask(task);
            return;
        }

        EnqueueContinuation(() => EnqueueTask(task), task);
    }

    public void Schedule(FiberTask task)
    {
        if (IsSchedulerThread)
            ScheduleLocal(task);
        else
            ScheduleFromAnyThread(task);
    }

    private void EnqueueContinuation(Action continuation, FiberTask? context)
    {
        _events.Writer.TryWrite((continuation, context));
        EnqueueWake();
    }

    public void ScheduleLocal(Action continuation, FiberTask? context = null)
    {
        AssertSchedulerThread();
        EnqueueContinuation(continuation, context);
    }

    public void ScheduleFromAnyThread(Action continuation, FiberTask? context = null)
    {
        EnqueueContinuation(continuation, context);
    }

    public void Schedule(Action continuation, FiberTask? context = null)
    {
        if (IsSchedulerThread)
            ScheduleLocal(continuation, context);
        else
            ScheduleFromAnyThread(continuation, context);
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

    public static TimerAwaiter Sleep(long ticks, KernelScheduler scheduler)
    {
        return new TimerAwaiter(ticks, scheduler);
    }

    public void Run(long maxTicks = -1)
    {
        BindOwnerThreadIfNeeded();
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
        foreach (var t in _tasks.Values)
            if (t.Status != FiberTaskStatus.Terminated && t.Status != FiberTaskStatus.Zombie)
            {
                anyAlive = true;
                if (t.Status == FiberTaskStatus.Waiting) anyWaiting = true;
            }

        return (anyAlive, anyWaiting);
    }

    public void Panic(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KERNEL PANIC: {reason}");
        sb.AppendLine($"CurrentTick: {CurrentTick}");
        sb.AppendLine($"RunQueue: {_runQueue.Count}");

        sb.AppendLine("Tasks:");
        foreach (var t in _tasks.Values)
            sb.AppendLine(
                $"  TID={t.TID} PID={t.PID} Status={t.Status} Exited={t.Exited} ExitStatus={t.ExitStatus}");

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

    private bool TryDequeue(out FiberTask? task)
    {
        return _runQueue.TryDequeue(out task!);
    }

    public void SignalProcessGroup(int pgid, int signal)
    {
        if (IsSchedulerThread)
            _ = SignalProcessGroupWithCount(pgid, signal);
        else
            ScheduleFromAnyThread(() => { _ = SignalProcessGroupWithCount(pgid, signal); });
    }

    public int SignalProcessGroupWithCount(int pgid, int signal)
    {
        AssertSchedulerThread();
        var count = 0;
        foreach (var p in _processes.Values)
            if (p.PGID == pgid)
            {
                var target = SelectSignalTarget(p, signal);
                if (target == null) continue;
                target.PostSignal(signal);
                count++;
            }

        return count;
    }

    public bool SignalProcess(int pid, int signal)
    {
        AssertSchedulerThread();
        if (!_processes.TryGetValue(pid, out var proc)) return false;

        var target = SelectSignalTarget(proc, signal);
        if (target != null)
        {
            target.PostSignal(signal);
            return true;
        }

        // Engine-managed init has no FiberTask. In --init mode, forward init's signals
        // to its direct children so kill(1, sig) semantics remain usable.
        if (EngineInitReaperEnabled && pid == InitPid) return ForwardSignalFromEngineInit(signal) > 0;

        return false;
    }

    public bool SignalProcessInfo(int pid, int signal, SigInfo info)
    {
        AssertSchedulerThread();
        if (!_processes.TryGetValue(pid, out var proc)) return false;

        var target = SelectSignalTarget(proc, signal);
        if (target != null)
        {
            target.PostSignalInfo(info);
            return true;
        }

        if (EngineInitReaperEnabled && pid == InitPid) return ForwardSignalFromEngineInit(signal) > 0;

        return false;
    }

    private static FiberTask? SelectSignalTarget(Process process, int signal)
    {
        FiberTask? leader = null;
        FiberTask? eligible = null;
        FiberTask? fallback = null;

        foreach (var task in process.Threads)
        {
            if (task.Status == FiberTaskStatus.Terminated || task.Exited) continue;

            if (fallback == null) fallback = task;
            if (task.TID == process.TGID) leader = task;

            if (!task.IsSignalIgnoredOrBlocked(signal))
            {
                if (task.TID == process.TGID) return task;
                if (eligible == null) eligible = task;
            }
        }

        return leader ?? eligible ?? fallback;
    }

    public int SignalAllProcesses(int signal, int? excludePid = null, bool skipInit = true)
    {
        AssertSchedulerThread();
        List<int> pids = [];
        foreach (var p in _processes.Values)
        {
            if (excludePid.HasValue && p.TGID == excludePid.Value) continue;
            if (skipInit && p.TGID == 1) continue;
            pids.Add(p.TGID);
        }

        var count = 0;
        foreach (var pid in pids)
            if (SignalProcess(pid, signal))
                count++;

        return count;
    }

    private int ForwardSignalFromEngineInit(int signal)
    {
        AssertSchedulerThread();
        if (!_processes.TryGetValue(InitPid, out var initProc)) return 0;

        var childPids = new List<int>(initProc.Children);

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
        if (IsSchedulerThread)
        {
            AssertSchedulerThread();
            var task = GetTask(tid);
            task?.PostSignal(signal);
            return;
        }

        ScheduleFromAnyThread(() =>
        {
            var task = GetTask(tid);
            task?.PostSignal(signal);
        });
    }

    public bool IsValidProcessGroup(int pgid, int sid)
    {
        AssertSchedulerThread();
        foreach (var p in _processes.Values)
            if (p.PGID == pgid && p.SID == sid)
                return true;

        return false;
    }
    public readonly struct TimerAwaiter(long ticks, KernelScheduler scheduler) : INotifyCompletion
    {
        private readonly long _ticks = ticks;
        private readonly KernelScheduler _scheduler = scheduler;

        public bool IsCompleted => _ticks <= 0;

        public void OnCompleted(Action continuation)
        {
            _scheduler.ScheduleTimer(_ticks, continuation);
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