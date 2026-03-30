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
    private const int RunQueueDrainEventBudget = 64;
    private static readonly ILogger Logger = Logging.CreateLogger<KernelScheduler>();
    private readonly Stack<AsyncWaitQueue> _asyncWaitQueuePool = new();

    // Global instance (or dependency injected)
    // public static KernelScheduler Instance { get; set; } = new();


    private readonly Channel<SchedulerWorkItem> _events =
        Channel.CreateUnbounded<SchedulerWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // Process Management
    private readonly Dictionary<int, Process> _processes = [];

    private readonly Queue<FiberTask> _runQueue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly KernelSyncContext _synchronizationContext;
    private readonly Dictionary<int, FiberTask> _tasks = [];
    private readonly TimeWheel _timerSystem = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);
    private int _engineInitReaperEnabled;
    private int _initPid;
    private bool _isInsideRunLoop = true;
    private int _nextTaskId;
    private int _ownerThreadId;

    private int _wakePending;

    public KernelScheduler()
    {
        _synchronizationContext = new KernelSyncContext(this);
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
            return (owner == 0 || owner == Environment.CurrentManagedThreadId) && _isInsideRunLoop;
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
        p.BindScheduler(this);
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
        var engine = process.Syscalls?.CurrentSyscallEngine;
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

        foreach (var task in tasksToRemove) ReleaseDetachedTask(task);
    }

    public int DetachTask(FiberTask task)
    {
        AssertSchedulerThread();
        _tasks.Remove(task.TID);

        ReleaseDetachedTask(task);
        return task.Process.Threads.Count;
    }

    private void ReleaseDetachedTask(FiberTask task)
    {
        task.CancelAsyncSyscallForRetirement();
        task.BeginTaskRetirement();
        task.Process.Syscalls?.UnregisterEngine(task.CPU);
        task.Status = FiberTaskStatus.Terminated;
        task.ExecutionMode = TaskExecutionMode.Terminated;
        task.Process.Threads.Remove(task);
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

    internal AsyncWaitQueue RentAsyncWaitQueue()
    {
        AssertSchedulerThread();
        if (_asyncWaitQueuePool.Count > 0)
        {
            var queue = _asyncWaitQueuePool.Pop();
            queue.ResetForReuse(this);
            return queue;
        }

        return new AsyncWaitQueue(this);
    }

    internal void ReturnAsyncWaitQueue(AsyncWaitQueue queue)
    {
        AssertSchedulerThread();
        queue.ResetForReuse(this);
        _asyncWaitQueuePool.Push(queue);
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
        HashSet<int> affectedProcessGroups = [];

        _processes.TryGetValue(fromPid, out fromProc);
        if (!_processes.TryGetValue(toPid, out toProc)) return 0;

        foreach (var proc in _processes.Values)
        {
            if (proc.PPID != fromPid) continue;
            proc.PPID = toPid;
            adoptedPids.Add(proc.TGID);
            if (proc.PGID > 0)
                affectedProcessGroups.Add(proc.PGID);
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
            SignalProcess(initTask.Process.TGID, (int)Signal.SIGCHLD);
            initTask.TrySetActiveWaitReason(WakeReason.Event);
            Schedule(initTask);
        }

        foreach (var pgid in affectedProcessGroups)
            NotifyNewlyOrphanedStoppedProcessGroup(pgid);

        return adoptedPids.Count;
    }

    private void NotifyNewlyOrphanedStoppedProcessGroup(int pgid)
    {
        AssertSchedulerThread();
        if (pgid <= 0) return;
        if (!IsOrphanedProcessGroup(pgid)) return;
        if (!ProcessGroupHasStoppedMembers(pgid)) return;

        _ = SignalProcessGroupWithCount(pgid, (int)Signal.SIGHUP);
        _ = SignalProcessGroupWithCount(pgid, (int)Signal.SIGCONT);
    }

    private bool IsOrphanedProcessGroup(int pgid)
    {
        AssertSchedulerThread();

        var foundMember = false;
        foreach (var process in _processes.Values)
        {
            if (process.PGID != pgid)
                continue;

            foundMember = true;

            if (process.PPID <= 0)
                continue;

            if (!_processes.TryGetValue(process.PPID, out var parent))
                continue;

            if (parent.SID == process.SID && parent.PGID != pgid)
                return false;
        }

        return foundMember;
    }

    private bool ProcessGroupHasStoppedMembers(int pgid)
    {
        AssertSchedulerThread();
        foreach (var process in _processes.Values)
            if (process.PGID == pgid && process.State == ProcessState.Stopped)
                return true;

        return false;
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

    public int TerminateRemainingProcessesForInitExit(int exitingPid)
    {
        AssertSchedulerThread();
        if (exitingPid <= 0 || exitingPid != InitPid) return 0;

        return SignalAllProcesses((int)Signal.SIGKILL, exitingPid, false);
    }

    public int ClearControllingTerminalForSession(TtyDiscipline tty, int sessionId)
    {
        AssertSchedulerThread();
        if (sessionId <= 0) return 0;

        var cleared = 0;
        foreach (var process in _processes.Values)
        {
            if (process.SID != sessionId) continue;
            if (!ReferenceEquals(process.ControllingTty, tty)) continue;

            process.ControllingTty = null;
            cleared++;
        }

        return cleared;
    }

    private void EnqueueTask(FiberTask task)
    {
        AssertSchedulerThread();
        if (task.Status == FiberTaskStatus.Terminated)
            return;

        if (task.IsRetiring)
            return;

        if (task.IsReadyQueued)
        {
            // We allow stale queue entries: a task can already have a run-queue slot reserved
            // and later transition back to Waiting before that slot is consumed. In that state
            // IsReadyQueued only means "a dequeue token exists", not "the task is currently Ready".
            //
            // A fresh wakeup must still make forward progress, so if the task is no longer Ready
            // we re-mark it as Ready and let the existing queue entry carry the wake. Skipping
            // here would drop the wakeup entirely until some unrelated event re-schedules it.
            if (task.Status != FiberTaskStatus.Ready)
                task.Status = FiberTaskStatus.Ready;

            if (!RunQueueContainsTask(task))
            {
                Logger.LogWarning(
                    "Ready-queue token state drift detected for TID={Tid}: IsReadyQueued=true but task missing from run queue. Re-enqueuing.",
                    task.TID);
                _runQueue.Enqueue(task);
                AssertReadyQueueInvariant(task, "EnqueueTask.repair");
                EnqueueWake();
                return;
            }

            AssertReadyQueueInvariant(task, "EnqueueTask.reuse");
            // The reserved queue token may already exist while the scheduler is asleep or spinning
            // on unrelated work. Re-signal the scheduler so this wakeup cannot be lost.
            EnqueueWake();
            return;
        }

        task.IsReadyQueued = true;
        task.Status = FiberTaskStatus.Ready;
        _runQueue.Enqueue(task);
        AssertReadyQueueInvariant(task, "EnqueueTask.new");

        EnqueueWake();
    }

    private bool RunQueueContainsTask(FiberTask task)
    {
        foreach (var queued in _runQueue)
            if (ReferenceEquals(queued, task))
                return true;

        return false;
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

        EnqueueWorkItem(SchedulerWorkItem.ResumeTask(task));
    }

    public void Schedule(FiberTask task)
    {
        if (IsSchedulerThread)
            ScheduleLocal(task);
        else
            ScheduleFromAnyThread(task);
    }

    private void EnqueueWorkItem(SchedulerWorkItem item)
    {
        _events.Writer.TryWrite(item);
        EnqueueWake();
    }

    private void EnqueueContinuation(Action continuation, FiberTask? context)
    {
        EnqueueWorkItem(SchedulerWorkItem.IngressAction(continuation, context));
    }

    public void ScheduleLocal(Action continuation, FiberTask? context = null)
    {
        AssertSchedulerThread();
        EnqueueContinuation(continuation, context);
    }

    public void ScheduleFromAnyThread(Action continuation, FiberTask? context = null)
    {
        EnqueueWorkItem(SchedulerWorkItem.IngressAction(continuation, context));
    }

    internal void RunIngress(Action action, FiberTask? context = null)
    {
        if (IsSchedulerThread)
        {
            if (context == null || !context.IsRetiring)
                action();
            return;
        }

        ScheduleFromAnyThread(action, context);
    }

    public void Schedule(Action continuation, FiberTask? context = null)
    {
        if (IsSchedulerThread)
            ScheduleLocal(continuation, context);
        else
            ScheduleFromAnyThread(continuation, context);
    }

    internal void Schedule(SchedulerWorkItem item)
    {
        // ResumeTask is just another way to request "make this task runnable". If we are already
        // on the scheduler thread, collapse that directly into EnqueueTask so the same stale-entry
        // rules apply regardless of where the wake originated.
        if (IsSchedulerThread && item.Kind == SchedulerWorkItemKind.ResumeTask && item.Task != null)
            EnqueueTask(item.Task);
        else
            EnqueueWorkItem(item);
    }

    internal void WakeTask(FiberTask task, FiberTask.WaitToken token, WakeReason reason, Action continuation)
    {
        if (IsSchedulerThread)
        {
            if (task.TrySetWaitReason(token, reason, false))
                ScheduleContinuation(continuation, task);
            return;
        }

        ScheduleFromAnyThread(() =>
        {
            if (task.TrySetWaitReason(token, reason, false))
                ScheduleContinuation(continuation, task);
        }, task);
    }

    internal void ScheduleContinuation(Action continuation, FiberTask task)
    {
        if (task.IsRetiring)
            return;

        // Wait/awaiter continuations are scheduler-thread callbacks associated with a task
        // context, not guest instruction slices. Run them through the ingress FIFO so they
        // preserve event order without mutating the task's runnable-slice state machine.
        Schedule(continuation, task);
    }

    internal void PostSynchronizationContext(SendOrPostCallback callback, object? state, FiberTask? context)
    {
        Schedule(SchedulerWorkItem.DispatchSyncContextPost(context, callback, state));
    }

    internal void SendSynchronizationContext(SendOrPostCallback callback, object? state, FiberTask? context)
    {
        if (IsSchedulerThread && CurrentTask == context)
        {
            callback(state);
            return;
        }

        using var request = new SyncContextSendRequest();
        Schedule(SchedulerWorkItem.DispatchSyncContextSend(context, callback, state, request));
        request.Wait();
        if (request.Thrown != null) throw request.Thrown;
    }

    internal FiberTask? CaptureCurrentTaskContext()
    {
        return CurrentTask;
    }

    private SynchronizationContext GetSynchronizationContextFor(FiberTask? task)
    {
        return task?.GetOrCreateSynchronizationContext() ?? _synchronizationContext;
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
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        Logger.LogInformation("KernelScheduler started.");
        var exitReason = "running=false";

        var startTick = _timerSystem.CurrentTick;

        try
        {
            while (Running)
            {
                _isInsideRunLoop = true;

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
                var hadRunnableTasks = _runQueue.Count > 0;
                var drainedEvents = DrainEventsWithBudget(hadRunnableTasks ? RunQueueDrainEventBudget : int.MaxValue);

                // 1. Process Timers & Wait
                if (_runQueue.Count == 0 && !drainedEvents)
                {
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
                    else
                    {
                        // If anyWaiting is true, we must wait indefinitely for external wakeups.
                        if (anyWaiting) waitTime = -1;
                        else
                            // Should not happen if anyAlive is true but runQueue empty (ValidateSchedulerState handles Ready tasks)
                            // Maybe just yield?
                            waitTime = 0;
                    }

                    // If waitTime is 0 (timer due now), we must wait at least 1ms to let wall clock advance
                    // otherwise we busy loop until _sw.ElapsedMilliseconds increases.
                    if (waitTime == 0) waitTime = 1;

                    if (OperatingSystem.IsBrowser())
                        throw new PlatformNotSupportedException(
                            "Synchronous Run is not supported on Browser Wasm. Use RunAsync instead.");

                    _isInsideRunLoop = false;
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
                    var previousTask = CurrentTask;
                    CurrentTask = task;
                    SynchronizationContext.SetSynchronizationContext(GetSynchronizationContextFor(task));
                    try
                    {
                        task.RunSlice();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                        CurrentTask = previousTask;
                    }

                    // Time accounting (simplified)
                    // _timerSystem.Advance(1); // Removed: Time is driven by _sw.ElapsedMilliseconds
                }
                else if (TryFindReadyTask(out var readyTaskAfterDequeueMiss))
                {
                    EnqueueTask(readyTaskAfterDequeueMiss!);
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

    public async Task RunAsync(long maxTicks = -1)
    {
        BindOwnerThreadIfNeeded();
        var previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        Logger.LogInformation("KernelScheduler async loop started.");
        var exitReason = "running=false";

        var startTick = _timerSystem.CurrentTick;
        var lastYieldMs = _sw.ElapsedMilliseconds;

        try
        {
            while (Running)
            {
                _isInsideRunLoop = true;

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
                var hadRunnableTasks = _runQueue.Count > 0;
                var drainedEvents = DrainEventsWithBudget(hadRunnableTasks ? RunQueueDrainEventBudget : int.MaxValue);

                // 1. Process Timers & Wait
                if (_runQueue.Count == 0 && !drainedEvents)
                {
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
                    else
                    {
                        // If anyWaiting is true, we must wait indefinitely for external wakeups.
                        if (anyWaiting) waitTime = -1;
                        else
                            waitTime = 0;
                    }

                    if (waitTime == 0)
                        // In some environments, a 0-wait is expensive/spinning. 
                        // But in Wasm, waitTime=0 usually means we should yield as much as possible.
                        // If it came from line 782 (timer already expired), we should spin once or yield.
                        // If it came from line 790 (no timers, nothing waiting), we probably should wait indefinitely for external wakeups.
                        waitTime = anyWaiting ? -1 : 1;

                    _isInsideRunLoop = false;
                    SynchronizationContext.SetSynchronizationContext(previousSyncContext);
                    await WaitForEventAsync((int)waitTime);
                    SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
                    continue;
                }

                // 2. Run Task
                if (TryDequeue(out var task) && task != null)
                {
                    if (task.Status != FiberTaskStatus.Ready)
                        continue;

                    task.Status = FiberTaskStatus.Running;

                    var previous = SynchronizationContext.Current;
                    var previousTask = CurrentTask;
                    CurrentTask = task;
                    SynchronizationContext.SetSynchronizationContext(GetSynchronizationContextFor(task));
                    try
                    {
                        task.RunSlice();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                        CurrentTask = previousTask;
                    }
                }
                else if (TryFindReadyTask(out var readyTaskAfterDequeueMiss))
                {
                    EnqueueTask(readyTaskAfterDequeueMiss!);
                }

                // Cooperative yield every so often to keep JS event loop responsive
                var currentMs = _sw.ElapsedMilliseconds;
                if (currentMs - lastYieldMs >= 10 && _runQueue.Count > 0)
                {
                    lastYieldMs = currentMs;
                    _isInsideRunLoop = false;
                    SynchronizationContext.SetSynchronizationContext(previousSyncContext);
                    await Task.Yield();
                    SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
                }
            }
        }
        catch (Exception ex)
        {
            exitReason = $"exception:{ex.GetType().Name}";
            Logger.LogError(ex, "KernelScheduler async loop crashed.");
            throw;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSyncContext);
            Logger.LogInformation("KernelScheduler async loop stopped. reason={Reason} running={Running} tick={Tick}",
                exitReason,
                Running, CurrentTick);
        }
    }

    private void DrainContinuations()
    {
        DrainEvents();
    }

    private bool DrainEvents()
    {
        return DrainEventsWithBudget(int.MaxValue);
    }

    private bool DrainEventsWithBudget(int maxItems)
    {
        var drained = false;
        var drainedCount = 0;
        while (drainedCount < maxItems && _events.Reader.TryRead(out var item))
        {
            drained = true;
            drainedCount++;
            ExecuteEvent(item);
        }

        if (drained && !_events.Reader.TryPeek(out _)) _wakeEvent.Reset();
        return drained;
    }

    private bool TryFindReadyTask(out FiberTask? readyTask)
    {
        foreach (var task in _tasks.Values)
            if (task.Status == FiberTaskStatus.Ready)
            {
                readyTask = task;
                return true;
            }

        readyTask = null;
        return false;
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

    private async ValueTask WaitForEventAsync(int timeoutMs)
    {
        if (timeoutMs < 0)
        {
            await _events.Reader.WaitToReadAsync().ConfigureAwait(false);
        }
        else
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await _events.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout elapsed
            }
        }

        _wakeEvent.Reset();
    }

    private void ExecuteEvent(SchedulerWorkItem item)
    {
        var oldTask = CurrentTask;
        CurrentTask = item.Context;
        var previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(GetSynchronizationContextFor(item.Context));
        try
        {
            switch (item.Kind)
            {
                case SchedulerWorkItemKind.ResumeTask:
                    if (item.Task != null && !item.Task.IsRetiring) EnqueueTask(item.Task);
                    break;
                case SchedulerWorkItemKind.IngressAction:
                    if (item.Context == null || !item.Context.IsRetiring)
                        item.Action?.Invoke();
                    break;
                case SchedulerWorkItemKind.WakeScheduler:
                    _wakePending = 0;
                    break;
                case SchedulerWorkItemKind.DispatchSyncContextPost:
                    if (item.Context == null || !item.Context.IsRetiring)
                        item.Callback?.Invoke(item.State);
                    break;
                case SchedulerWorkItemKind.DispatchSyncContextSend:
                    try
                    {
                        if (item.Context == null || !item.Context.IsRetiring)
                            item.Callback?.Invoke(item.State);
                    }
                    catch (Exception ex)
                    {
                        if (item.SendRequest != null) item.SendRequest.Thrown = ex;
                    }
                    finally
                    {
                        item.SendRequest?.SetCompleted();
                    }

                    break;
                case SchedulerWorkItemKind.FinalizeTaskRetirement:
                    item.Task?.TryFinalizeTaskRetirement();
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported work item kind {item.Kind}.");
            }
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
        _events.Writer.TryWrite(SchedulerWorkItem.WakeScheduler());
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
        if (!_runQueue.TryDequeue(out task!))
            return false;

        if (task != null)
            // Dequeue consumes the single queued wake token for this task. The task may still be
            // Waiting here because the queue item can be stale; callers decide whether to run it
            // based on the current Status.
            task.IsReadyQueued = false;

        return true;
    }

    [Conditional("DEBUG")]
    private static void AssertReadyQueueInvariant(FiberTask task, string site)
    {
        Debug.Assert(task.IsReadyQueued, $"[{site}] queued task must set IsReadyQueued.");
        Debug.Assert(task.Status == FiberTaskStatus.Ready,
            $"[{site}] queued task must be Ready after enqueue. actual={task.Status}");
    }

    public void SignalProcessGroup(int pgid, int signal)
    {
        if (IsSchedulerThread)
            _ = SignalProcessGroupWithCount(pgid, signal);
        else
            SignalProcessGroupFromAnyThread(pgid, signal);
    }

    public void SignalProcessGroupFromAnyThread(int pgid, int signal)
    {
        RunIngress(() => { _ = SignalProcessGroupWithCount(pgid, signal); });
    }

    public int SignalProcessGroupWithCount(int pgid, int signal)
    {
        AssertSchedulerThread();
        var count = 0;
        foreach (var p in _processes.Values)
            if (p.PGID == pgid)
                if (PostProcessSignalInfo(p, new SigInfo { Signo = signal, Code = 0 }))
                    count++;

        return count;
    }

    public bool SignalProcess(int pid, int signal)
    {
        AssertSchedulerThread();
        if (!_processes.TryGetValue(pid, out var proc)) return false;

        if (EngineInitReaperEnabled && pid == InitPid && SelectSignalWakeTarget(proc, signal) == null)
            return ForwardSignalFromEngineInit(signal) > 0;

        if (PostProcessSignalInfo(proc, new SigInfo { Signo = signal, Code = 0 }))
            return true;

        // Engine-managed init has no FiberTask. In --init mode, forward init's signals
        // to its direct children so kill(1, sig) semantics remain usable.
        if (EngineInitReaperEnabled && pid == InitPid) return ForwardSignalFromEngineInit(signal) > 0;

        return false;
    }

    public bool SignalProcessInfo(int pid, int signal, SigInfo info)
    {
        AssertSchedulerThread();
        if (!_processes.TryGetValue(pid, out var proc)) return false;

        if (EngineInitReaperEnabled && pid == InitPid && SelectSignalWakeTarget(proc, signal) == null)
            return ForwardSignalFromEngineInit(signal) > 0;

        if (PostProcessSignalInfo(proc, info))
            return true;

        if (EngineInitReaperEnabled && pid == InitPid) return ForwardSignalFromEngineInit(signal) > 0;

        return false;
    }

    internal bool PostProcessSignalInfo(Process process, SigInfo info)
    {
        var signal = info.Signo;
        if (signal < 1 || signal > 64) return false;

        if (signal != (int)Signal.SIGKILL && signal != (int)Signal.SIGSTOP)
        {
            if (process.SignalActions.TryGetValue(signal, out var action))
            {
                if (action.Handler == 1) return true;
            }
            else
            {
                var defaultAction = signal switch
                {
                    (int)Signal.SIGCHLD or (int)Signal.SIGURG or (int)Signal.SIGWINCH => true,
                    _ => false
                };
                if (defaultAction) return true;
            }
        }

        if (!process.EnqueueProcessSignal(info)) return false;

        var target = SelectSignalWakeTarget(process, signal);
        if (target == null) return true;

        target.NotifyPendingSignal(signal);
        return true;
    }

    private static FiberTask? SelectSignalWakeTarget(Process process, int signal)
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

        return eligible ?? leader ?? fallback;
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

        SignalTaskFromAnyThread(tid, signal);
    }

    public void SignalTaskFromAnyThread(int tid, int signal)
    {
        RunIngress(() =>
        {
            var task = GetTask(tid);
            task?.PostSignal(signal);
        });
    }

    public void SignalTaskFromAnyThread(FiberTask task, int signal)
    {
        RunIngress(() => task.PostSignal(signal), task);
    }

    internal void PostSignalInfoFromAnyThread(FiberTask task, SigInfo info)
    {
        RunIngress(() => task.PostSignalInfoCore(info), task);
    }

    public bool ProcessGroupExists(int pgid)
    {
        AssertSchedulerThread();
        foreach (var p in _processes.Values)
            if (p.PGID == pgid)
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