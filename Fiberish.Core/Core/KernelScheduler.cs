using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiberish.Core;

public class KernelScheduler : IDisposable
{
    private const int DefaultInstructionLimit = 10_000_000;
    private const int InteractiveBurstBudget = 3;
    private const int InteractiveInstructionLimit = 250_000;
    private const int LatencySensitiveInstructionLimit = 1_000_000;
    private const long ForegroundLatencyGraceMs = 50;
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
    private readonly Channel<SchedulerWorkItem> _urgentEvents =
        Channel.CreateUnbounded<SchedulerWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // Process Management
    private readonly Dictionary<int, Process> _processes = [];

    private readonly Queue<FiberTask> _foregroundRunQueue = new();
    private readonly Queue<FiberTask> _runQueue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly KernelSyncContext _synchronizationContext;
    private readonly Dictionary<int, FiberTask> _tasks = [];
    private readonly TimeWheel _timerSystem = new();
    private readonly AutoResetEvent _wakeSignal = new(false);
    private readonly HighResolutionWait _preciseWait = new();
    private int _engineInitReaperEnabled;
    private int _initPid;
    private bool _isInsideRunLoop = true;
    private int _nextTaskId;
    private int _ownerThreadId;
    private long _runStartTick;
    private int _waitingForWake;

    private long _foregroundLatencyGraceUntilTick;
    private int _wakePending;
    private long _wakeSetCount;
    private volatile bool _disposed;

    public KernelScheduler()
    {
        _synchronizationContext = new KernelSyncContext(this);
    }

    /// <summary>
    ///     Releases the wake event and the native high-resolution timer. Call this after
    ///     <see cref="Run" /> returns, when no wait is in progress on the scheduler thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preciseWait.Dispose();
        _wakeSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    public KernelRuntime? Runtime { get; set; }

    public long CurrentTick => _timerSystem.CurrentTick;

    /// <summary>Milliseconds since scheduler start; matches the reference used by timer deadlines.</summary>
    public long CurrentRealTick => _sw.ElapsedMilliseconds;

    /// <summary>Number of native wait-handle Set calls issued by the scheduler.</summary>
    internal long WakeSetCount => Interlocked.Read(ref _wakeSetCount);
    internal TimeWheel TimerSystem => _timerSystem;
    public int PipeMaxSize { get; set; } = 1048576;
    public int PipeUserPagesSoft { get; set; } = 16384;
    public int PipeUserPagesHard { get; set; }
    private readonly object _pipeUserPageGate = new();
    private readonly Dictionary<int, long> _pipeUserPages = [];

    public int ReservePipePages(int uid, int requestedPages)
    {
        if (uid < 0 || requestedPages <= 0) return requestedPages;

        lock (_pipeUserPageGate)
        {
            _pipeUserPages.TryGetValue(uid, out var current);
            var hard = PipeUserPagesHard;
            var soft = PipeUserPagesSoft;
            var granted = requestedPages;

            // A hard limit rejects the new pipe even if the soft limit would
            // otherwise shrink it to a single page.
            if (hard > 0 && current + 1 > hard)
                throw new PipeQuotaExceededException();

            if (soft > 0 && current + granted > soft)
                granted = 1;
            if (hard > 0 && current + granted > hard)
                throw new PipeQuotaExceededException();

            _pipeUserPages[uid] = current + granted;
            return granted;
        }
    }

    public bool TryReservePipePages(int uid, int pages)
    {
        if (uid < 0 || pages <= 0) return true;

        lock (_pipeUserPageGate)
        {
            _pipeUserPages.TryGetValue(uid, out var current);
            var hard = PipeUserPagesHard;
            if (hard > 0 && current + pages > hard)
                return false;

            _pipeUserPages[uid] = current + pages;
            return true;
        }
    }

    public void ReleasePipePages(int uid, int pages)
    {
        if (uid < 0 || pages <= 0) return;

        lock (_pipeUserPageGate)
        {
            if (!_pipeUserPages.TryGetValue(uid, out var current)) return;
            var remaining = current - pages;
            if (remaining <= 0)
                _pipeUserPages.Remove(uid);
            else
                _pipeUserPages[uid] = remaining;
        }
    }
    public bool Running { get; set; } = true;

    public ILoggerFactory LoggerFactory { get; set; } = new NullLoggerFactory();

    public FiberTask? CurrentTask { get; internal set; }
    public int InitPid => Volatile.Read(ref _initPid);
    public bool EngineInitReaperEnabled => Volatile.Read(ref _engineInitReaperEnabled) != 0;
    public int ProcessCount
    {
        get
        {
            AssertSchedulerThread();
            return _processes.Count;
        }
    }


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

    internal long GetPendingSignalCountForUser(int uid)
    {
        AssertSchedulerThread();
        long count = 0;
        foreach (var process in _processes.Values)
            count += process.GetPendingSignalCount(uid);
        return count;
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

        var beforeBytes = process.Mem.MemoryContext.GetTotalTrackedBytes();
        var beforeByClass = process.Mem.MemoryContext.GetAllocationClassStatsSummary();
        var (cowFirstBefore, cowReplaceBefore) = VMAManager.GetCowAllocationCounters();
        var refsBefore = process.Mem.GetSharedRefCount();
        var refsAfter = process.Mem.ReleaseSharedRef(engine);
        process.MemoryReleased = true;
        var afterBytes = process.Mem.MemoryContext.GetTotalTrackedBytes();
        var afterByClass = process.Mem.MemoryContext.GetAllocationClassStatsSummary();
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

    /// <summary>
    ///     Linux de_thread(): successful execve keeps the caller and removes every other
    ///     task in the thread group before the new image starts.
    /// </summary>
    public int DetachSiblingThreads(FiberTask current)
    {
        AssertSchedulerThread();
        var threads = current.Process.Threads;
        if (threads.Count <= 1) return threads.Count;

        var siblings = new List<FiberTask>(threads.Count - 1);
        foreach (var thread in threads)
            if (!ReferenceEquals(thread, current))
                siblings.Add(thread);

        foreach (var sibling in siblings)
        {
            // Linux delivers these through do_exit() for every thread zapped by de_thread().
            sibling.ExitRobustList();
            sibling.RobustListHead = 0;
            sibling.RobustListSize = 0;
            sibling.ClearChildTidAndWake();
            sibling.Exited = true;
            sibling.ExitStatus = 0;
            DetachTask(sibling);
        }

        return current.Process.Threads.Count;
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
        task.EndGuestCpuAccounting();
        var cpu = task.SnapshotThreadCpuTime();
        // The task's slices are already in the live bucket; move them to the exited bucket.
        task.Process.RemoveAccumulatedLiveCpuTime(cpu);
        task.Process.AccumulateExitedThreadCpuTime(cpu);
        task.CancelAsyncSyscallForRetirement();
        task.BeginTaskRetirement();
        if (task.Process.Threads.Count == 1)
            task.Process.Syscalls?.ReleasePosixLocksForProcess(task.Process);
        task.Process.Syscalls?.ReleaseTaskFileDescriptors(task);
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

        foreach (var proc in _processes.Values)
        {
            if (proc.PPID == exitingPid && proc.ParentDeathSignal > 0 &&
                proc.State is not ProcessState.Zombie and not ProcessState.Dead)
            {
                SignalProcess(proc.TGID, proc.ParentDeathSignal);
            }
        }

        var targetPid = initPid;
        if (_processes.TryGetValue(exitingPid, out var exitingProc))
        {
            var cursor = exitingProc.PPID;
            var hops = 0;
            while (cursor > 0 && cursor != initPid && hops++ < 1024 && _processes.TryGetValue(cursor, out var ancestor))
            {
                if (ancestor.IsChildSubreaper && ancestor.State is not ProcessState.Zombie and not ProcessState.Dead)
                {
                    targetPid = ancestor.TGID;
                    break;
                }

                cursor = ancestor.PPID;
            }
        }

        return ReparentChildren(exitingPid, targetPid);
    }

    public int ReparentChildren(int fromPid, int toPid)
    {
        AssertSchedulerThread();
        if (fromPid <= 0 || toPid <= 0 || fromPid == toPid) return 0;

        Process? fromProc;
        Process? toProc;
        List<int> adoptedPids = [];
        HashSet<int> affectedProcessGroups = [];
        var adoptedHasZombie = false;

        _processes.TryGetValue(fromPid, out fromProc);
        if (!_processes.TryGetValue(toPid, out toProc)) return 0;

        foreach (var proc in _processes.Values)
        {
            if (proc.PPID != fromPid) continue;
            proc.PPID = toPid;
            adoptedPids.Add(proc.TGID);
            if (proc.State == ProcessState.Zombie)
                adoptedHasZombie = true;
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

        var targetTask = GetTask(toPid);
        if (targetTask != null && (toPid == InitPid || adoptedHasZombie))
        {
            SignalProcess(targetTask.Process.TGID, (int)Signal.SIGCHLD);
            targetTask.TrySetActiveWaitReason(WakeReason.Event);
            Schedule(targetTask);
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

        initProc!.AccumulateChildrenCpuTime(process.GetReapedCpuTimeSnapshot());
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
                EnqueueReadyTask(task);
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
        EnqueueReadyTask(task);
        AssertReadyQueueInvariant(task, "EnqueueTask.new");

        EnqueueWake();
    }

    private void EnqueueReadyTask(FiberTask task)
    {
        if (ShouldUseForegroundQueue(task))
            _foregroundRunQueue.Enqueue(task);
        else
            _runQueue.Enqueue(task);
    }

    private bool RunQueueContainsTask(FiberTask task)
    {
        foreach (var queued in _foregroundRunQueue)
            if (ReferenceEquals(queued, task))
                return true;

        foreach (var queued in _runQueue)
            if (ReferenceEquals(queued, task))
                return true;

        return false;
    }

    private static bool IsForegroundTask(FiberTask? task)
    {
        if (task?.Process.ControllingTty is not { } tty)
            return false;

        return tty.ForegroundPgrp > 0 && task.Process.PGID == tty.ForegroundPgrp;
    }

    private bool HasForegroundLatencyGrace
    {
        get
        {
            return Volatile.Read(ref _foregroundLatencyGraceUntilTick) > _timerSystem.CurrentTick;
        }
    }

    private bool ShouldScheduleUrgent(FiberTask? task)
    {
        return task != null && IsForegroundTask(task) && (task.HasInteractiveBurstBudget || HasForegroundLatencyGrace);
    }

    private bool ShouldUseForegroundQueue(FiberTask task)
    {
        return IsForegroundTask(task) && (task.HasInteractiveBurstBudget || HasForegroundLatencyGrace);
    }

    private void ExtendForegroundLatencyGrace()
    {
        var currentTick = _timerSystem.CurrentTick;
        var candidate = currentTick + ForegroundLatencyGraceMs;
        var observed = Volatile.Read(ref _foregroundLatencyGraceUntilTick);
        while (observed < candidate)
        {
            var previous = Interlocked.CompareExchange(ref _foregroundLatencyGraceUntilTick, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    internal void NoteInteractiveWake(FiberTask task)
    {
        AssertSchedulerThread();
        if (IsForegroundTask(task))
        {
            task.SeedInteractiveBurst(InteractiveBurstBudget);
            ExtendForegroundLatencyGrace();
        }
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

        EnqueueWorkItem(SchedulerWorkItem.ResumeTask(task), ShouldScheduleUrgent(task));
    }

    public void Schedule(FiberTask task)
    {
        if (IsSchedulerThread)
            ScheduleLocal(task);
        else
            ScheduleFromAnyThread(task);
    }

    private void EnqueueWorkItem(SchedulerWorkItem item, bool urgent = false)
    {
        if (urgent)
            _urgentEvents.Writer.TryWrite(item);
        else
            _events.Writer.TryWrite(item);
        EnqueueWake();
    }

    private void EnqueueContinuation(Action continuation, FiberTask? context, bool urgent = false)
    {
        EnqueueWorkItem(SchedulerWorkItem.IngressAction(continuation, context), urgent);
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

    internal void ScheduleLocal(Action continuation, FiberTask? context, bool urgent)
    {
        AssertSchedulerThread();
        EnqueueContinuation(continuation, context, urgent);
    }

    internal void ScheduleFromAnyThread(Action continuation, FiberTask? context, bool urgent)
    {
        EnqueueWorkItem(SchedulerWorkItem.IngressAction(continuation, context), urgent);
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

    internal void Schedule(Action continuation, FiberTask? context, bool urgent)
    {
        if (IsSchedulerThread)
            ScheduleLocal(continuation, context, urgent);
        else
            ScheduleFromAnyThread(continuation, context, urgent);
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
            {
                NoteInteractiveWake(task);
                ScheduleContinuation(continuation, task);
            }
            return;
        }

        ScheduleFromAnyThread(() =>
        {
            if (task.TrySetWaitReason(token, reason, false))
            {
                NoteInteractiveWake(task);
                ScheduleContinuation(continuation, task);
            }
        }, task, IsForegroundTask(task));
    }

    internal void ScheduleContinuation(Action continuation, FiberTask task)
    {
        if (task.IsRetiring)
            return;

        // Wait/awaiter continuations are scheduler-thread callbacks associated with a task
        // context, not guest instruction slices. Run them through the ingress FIFO so they
        // preserve event order without mutating the task's runnable-slice state machine.
        Schedule(continuation, task, ShouldScheduleUrgent(task));
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
        // Map the request onto the scheduler tick axis. Advance() fires the timers of tick T
        // when it moves the clock past T, so the tick that wakes at ceil(deadline) is one less.
        // This never fires before the requested delay and does not lose a whole millisecond.
        var elapsedMs = _sw.Elapsed.TotalMilliseconds;
        var targetTick = _runStartTick + (long)Math.Ceiling(elapsedMs + delayMs) - 1;
        // Keep the wheel's own tick axis authoritative too: a test or a caller may advance
        // the timer wheel ahead of wall time, and the delay must still be honoured there.
        var wheelTick = _timerSystem.CurrentTick + delayMs;
        if (targetTick < wheelTick) targetTick = wheelTick;
        // A timer placed in the tick that Advance() is currently firing would sit in an
        // already-drained slot and only fire on the next wheel wrap (256 ticks later).
        var earliestTick = _timerSystem.CurrentTick + 1;
        if (targetTick < earliestTick) targetTick = earliestTick;

        if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace(
                "[Scheduler] ScheduleTimer delayMs={DelayMs} currentTick={CurrentTick} elapsedMs={ElapsedMs} targetTick={TargetTick}",
                delayMs, _timerSystem.CurrentTick, elapsedMs, targetTick);
        }
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

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    public void Run(long maxTicks = -1)
    {
        BindOwnerThreadIfNeeded();
        var previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        Logger.LogInformation("KernelScheduler started.");
        var exitReason = "running=false";

        var startTick = _timerSystem.CurrentTick;
        _runStartTick = startTick;
        var winTimerActive = false;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                TimeBeginPeriod(1);
                winTimerActive = true;
            }
            catch
            {
            }
        }

        try
        {
            while (Running)
            {
                _isInsideRunLoop = true;

                // Advance timer to current physical time (in ms)
                var nowMs = startTick + _sw.ElapsedMilliseconds;
                _timerSystem.Advance(nowMs);
                if (OperatingSystem.IsBrowser())
                    BrowserSchedulerHostBridge.PollHttpRpc();

                if (maxTicks > 0 && CurrentTick >= maxTicks)
                {
                    Logger.LogWarning("KernelScheduler reached max ticks limit. Stopping.");
                    exitReason = "max-ticks";
                    break;
                }

                // 0. Process Continuations (High Priority)
                var hadRunnableTasks = _foregroundRunQueue.Count > 0 || _runQueue.Count > 0;
                var drainedEvents = DrainEventsWithBudget(hadRunnableTasks ? RunQueueDrainEventBudget : int.MaxValue);

                // 1. Process Timers & Wait
                if (_foregroundRunQueue.Count == 0 && _runQueue.Count == 0 && !drainedEvents)
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
                    var infiniteWait = false;
                    var waitTimeMs = 0.0;
                    if (_timerSystem.HasTimers)
                    {
                        var nextTick = _timerSystem.GetNextExpiration();
                        if (nextTick.HasValue)
                        {
                            // Advance() fires a tick's timers while it moves the clock past that
                            // tick, so the wakeup lands on the wall-clock instant at which the
                            // next tick begins. Waiting for that exact instant avoids both the
                            // system timer tick (15.6ms) and a whole missed millisecond.
                            var elapsedMs = _sw.Elapsed.TotalMilliseconds;
                            waitTimeMs = nextTick.Value - _runStartTick + 1 - elapsedMs;
                            if (waitTimeMs <= 0)
                                // Timer belongs to the tick that is already current: wait for
                                // the next millisecond boundary so Advance() can move past it.
                                waitTimeMs = Math.Max(0.05, Math.Floor(elapsedMs) + 1 - elapsedMs);
                        }
                    }
                    else
                    {
                        // If anyWaiting is true, we must wait indefinitely for external wakeups.
                        if (anyWaiting) infiniteWait = true;
                        else
                            // Should not happen if anyAlive is true but runQueue empty (ValidateSchedulerState handles Ready tasks)
                            // Maybe just yield?
                            waitTimeMs = 0.5;
                    }

                    _isInsideRunLoop = false;
                    WaitForEvent(waitTimeMs, infiniteWait);
                    continue;
                }

                // 2. Run Task
                if (TryDequeueInternal(out var task, out var foregroundDispatch) && task != null)
                {
                    // Stale queue entry: task may have transitioned to Waiting/Terminated
                    // after it was enqueued (e.g., async syscall path). Skip safely.
                    if (task.Status != FiberTaskStatus.Ready)
                        continue;

                    var interactiveDispatch = foregroundDispatch || ShouldUseForegroundQueue(task);
                    if (interactiveDispatch)
                        task.ConsumeInteractiveBurstDispatch();

                    task.Status = FiberTaskStatus.Running;

                    // Execute a slice
                    // We advance time based on how much work the task did
                    var previous = SynchronizationContext.Current;
                    var previousTask = CurrentTask;
                    CurrentTask = task;
                    var process = task.Process;
                    process.RunningTask = task;
                    SynchronizationContext.SetSynchronizationContext(GetSynchronizationContextFor(task));
                    try
                    {
                        var instructionLimit = interactiveDispatch
                            ? InteractiveInstructionLimit
                            : HasForegroundLatencyGrace
                                ? LatencySensitiveInstructionLimit
                                : DefaultInstructionLimit;
                        task.RunSlice(instructionLimit);
                    }
                    finally
                    {
                        if (ReferenceEquals(process.RunningTask, task))
                            process.RunningTask = null;
                        SynchronizationContext.SetSynchronizationContext(previous);
                        CurrentTask = previousTask;
                    }

                    CheckProcessCpuAccounting(task.Process);

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
            if (winTimerActive)
            {
                try
                {
                    TimeEndPeriod(1);
                }
                catch
                {
                }
            }

            SynchronizationContext.SetSynchronizationContext(previousSyncContext);
            Logger.LogInformation("KernelScheduler stopped. reason={Reason} running={Running} tick={Tick}", exitReason,
                Running, CurrentTick);
        }
    }

    private void CheckProcessCpuAccounting(Process process)
    {
        if (!process.NeedsCpuTimeAccounting)
            return;

        // Refreshing at most once per real millisecond keeps the rlimit (seconds) and the
        // CPU itimers (milliseconds) accurate while avoiding one O(threads) walk per slice
        // per running thread.
        var nowMs = _sw.ElapsedMilliseconds;
        if (process.CpuAccountingSnapshotMs != nowMs)
        {
            process.CpuAccountingSnapshot = process.GetSelfCpuTimeSnapshot();
            process.CpuAccountingSnapshotMs = nowMs;
        }

        var snapshot = process.CpuAccountingSnapshot;
        EnforceCpuTimeLimit(process, snapshot);
        Fiberish.Syscalls.SyscallManager.CheckCpuItimers(process, this, snapshot);
    }

    private void EnforceCpuTimeLimit(Process process, CpuTimeSnapshot snap)
    {
        if (process.State is ProcessState.Zombie or ProcessState.Dead)
            return;

        if (!process.TryGetResourceLimit(LinuxConstants.RLIMIT_CPU, out var cpuLimit))
            return;

        if (cpuLimit.Soft == LinuxConstants.RLIM64_INFINITY && cpuLimit.Hard == LinuxConstants.RLIM64_INFINITY)
            return;

        var cpuSec = (snap.UserNs + snap.SystemNs) / 1_000_000_000L;

        if (cpuLimit.Hard != LinuxConstants.RLIM64_INFINITY && (ulong)cpuSec >= cpuLimit.Hard)
        {
            SignalProcess(process.TGID, (int)Signal.SIGKILL);
            return;
        }

        if (cpuLimit.Soft != LinuxConstants.RLIM64_INFINITY && (ulong)cpuSec >= cpuLimit.Soft)
        {
            if (cpuSec > process.LastCpuSoftWarningSec)
            {
                process.LastCpuSoftWarningSec = cpuSec;
                SignalProcess(process.TGID, (int)Signal.SIGXCPU);
            }
        }
    }

    private bool DrainEventsWithBudget(int maxItems)
    {
        var drained = false;
        drained |= DrainEventChannelWithBudget(_urgentEvents, int.MaxValue);
        drained |= DrainEventChannelWithBudget(_events, maxItems);
        return drained;
    }

    private bool DrainEventChannelWithBudget(Channel<SchedulerWorkItem> channel, int maxItems)
    {
        var drained = false;
        var drainedCount = 0;
        while (drainedCount < maxItems && channel.Reader.TryRead(out var item))
        {
            drained = true;
            drainedCount++;
            ExecuteEvent(item);
        }

        return drained;
    }

    private bool DrainEvents()
    {
        return DrainEventsWithBudget(int.MaxValue);
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

    private void WaitForEvent(double timeoutMs, bool infinite)
    {
        if (_disposed)
            return;

        if (OperatingSystem.IsBrowser() && BrowserSchedulerHostBridge.IsConfigured)
        {
            BrowserSchedulerHostBridge.WaitForEvent(infinite ? -1 : (int)Math.Max(1, Math.Ceiling(timeoutMs)));
            return;
        }

        // Publish the waiter flag before re-checking the work queues. A waker
        // publishes work before reading this flag, so either it sees the waiter
        // and sets the event, or the recheck below sees the work and skips the wait.
        Volatile.Write(ref _waitingForWake, 1);
        try
        {
            if (HasPendingWakeWork())
                return;

            _preciseWait.Wait(_wakeSignal, timeoutMs, infinite);
        }
        finally
        {
            Volatile.Write(ref _waitingForWake, 0);
        }
    }

    private bool HasPendingWakeWork()
    {
        return _foregroundRunQueue.Count > 0 || _runQueue.Count > 0 ||
               _events.Reader.TryPeek(out _) || _urgentEvents.Reader.TryPeek(out _);
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
        if (_disposed)
            return;

        if (OperatingSystem.IsBrowser() && BrowserSchedulerHostBridge.IsConfigured)
            BrowserSchedulerHostBridge.SignalSchedulerWake();

        // Publish pending work before reading the waiter flag: a waiter that already peeked
        // past this item must observe the flag and be roused by the OS signal below.
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            _events.Writer.TryWrite(SchedulerWorkItem.WakeScheduler());

        // Only enter the kernel when a waiter is actually blocked. The waiter rechecks
        // the work queues after publishing _waitingForWake, so work published before
        // this read cannot be lost even when no Set is issued.
        if (Volatile.Read(ref _waitingForWake) != 0)
        {
            Interlocked.Increment(ref _wakeSetCount);
            try
            {
                _wakeSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
        sb.AppendLine($"RunQueue: {_foregroundRunQueue.Count + _runQueue.Count}");

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

    private bool TryDequeueInternal(out FiberTask? task, out bool foregroundDispatch)
    {
        if (_foregroundRunQueue.TryDequeue(out task!))
        {
            foregroundDispatch = true;
        }
        else
        {
            foregroundDispatch = false;
            if (!_runQueue.TryDequeue(out task!))
                return false;
        }

        if (task != null)
            // Dequeue consumes the single queued wake token for this task. The task may still be
            // Waiting here because the queue item can be stale; callers decide whether to run it
            // based on the current Status.
            task.IsReadyQueued = false;

        return true;
    }

    private bool TryDequeue(out FiberTask? task)
    {
        return TryDequeueInternal(out task, out _);
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
            var ignored = false;
            if (process.SignalActions.TryGetValue(signal, out var action))
            {
                ignored = action.Handler == 1;
            }
            else
            {
                ignored = signal is (int)Signal.SIGCHLD or (int)Signal.SIGURG or (int)Signal.SIGWINCH;
            }

            // A blocked ignored signal remains waitable through sigwaitinfo.
            if (ignored && !process.Threads.Any(t => t.IsSignalMasked(signal)))
                return true;
        }

        if (!process.EnqueueProcessSignal(info)) return false;

        var target = SelectSignalWakeTarget(process, signal);
        if (target == null) return true;

        target.NotifyPendingSignal(signal);
        return true;
    }

    private static FiberTask? SelectSignalWakeTarget(Process process, int signal)
    {
        FiberTask? signalWaitLeader = null;
        FiberTask? signalWaitEligible = null;
        FiberTask? leader = null;
        FiberTask? eligibleLeader = null;
        FiberTask? eligible = null;
        FiberTask? fallback = null;

        foreach (var task in process.Threads)
        {
            if (task.Status == FiberTaskStatus.Terminated || task.Exited) continue;

            if (fallback == null) fallback = task;
            if (task.TID == process.TGID) leader = task;

            if (task.PrefersSignalWake(signal))
            {
                if (task.TID == process.TGID)
                    signalWaitLeader = task;
                else if (signalWaitEligible == null)
                    signalWaitEligible = task;
            }

            if (!task.IsSignalIgnoredOrBlocked(signal))
            {
                if (task.TID == process.TGID)
                    eligibleLeader ??= task;
                else
                    eligible ??= task;
            }
        }

        return signalWaitLeader ?? signalWaitEligible ?? eligibleLeader ?? eligible ?? leader ?? fallback;
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
        public void OnCompleted(Action continuation)
        {
            scheduler.ScheduleTimer(ticks, continuation);
        }
    }

    public class KernelPanicException : Exception
    {
        public KernelPanicException(string message) : base(message)
        {
        }
    }
}
