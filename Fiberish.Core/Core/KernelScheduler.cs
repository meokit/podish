using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;

namespace Bifrost.Core;

public class KernelScheduler
{
    private static readonly ILogger Logger = Logging.CreateLogger<KernelScheduler>();
    
    private readonly Queue<FiberTask> _runQueue = new();
    private readonly TimerSystem _timerSystem = new();
    
    public long CurrentTick => _timerSystem.CurrentTick;
    public bool Running { get; set; } = true;
    
    // Process Management
    private readonly Dictionary<int, Process> _processes = new();
    private readonly Dictionary<int, FiberTask> _tasks = new();
    
    public FiberTask? CurrentTask { get; internal set; }

    public void RegisterProcess(Process p)
    {
        _processes[p.TGID] = p;
    }

    public void RegisterTask(FiberTask t)
    {
        _tasks[t.TID] = t;
    }
    
    public Process? GetProcess(int pid)
    {
        return _processes.TryGetValue(pid, out var p) ? p : null;
    }

    public FiberTask? GetTask(int tid)
    {
        return _tasks.TryGetValue(tid, out var t) ? t : null;
    }

    // Global instance (or dependency injected)
    public static KernelScheduler Instance { get; private set; } = new();

    public KernelScheduler()
    {
        Instance = this;
    }

    public void Schedule(FiberTask task)
    {
        if (task.Status == FiberTaskStatus.Terminated) return;
        
        task.Status = FiberTaskStatus.Ready;
        _runQueue.Enqueue(task);
    }

    public Timer ScheduleTimer(long delayTicks, Action callback)
    {
        return _timerSystem.Schedule(delayTicks, callback);
    }
    
    public TimerAwaiter Sleep(long ticks) => new TimerAwaiter(ticks);

    public struct TimerAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        private readonly long _ticks;
        public TimerAwaiter(long ticks) => _ticks = ticks;
        public bool IsCompleted => _ticks <= 0;
        public void OnCompleted(Action continuation) => KernelScheduler.Instance.ScheduleTimer(_ticks, continuation);
        public void GetResult() { }
    }

    public void Run()
    {
        Logger.LogInformation("KernelScheduler started.");
        
        while (Running)
        {
            // 1. Process Timers
            // If the queue is empty, we MUST advance time to the next timer event
            // to avoid busy loop deadlocks if all tasks are waiting on timers.
            if (_runQueue.Count == 0)
            {
                var nextWakeup = _timerSystem.NextExpiration;
                if (nextWakeup.HasValue)
                {
                    long jump = nextWakeup.Value - CurrentTick;
                    if (jump > 0)
                    {
                        _timerSystem.Advance(jump);
                    }
                }
                else
                {
                    // No tasks and no timers? We are done or deadlocked.
                    if (Running)
                    {
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
            if (_runQueue.TryDequeue(out var task))
            {
                task.Status = FiberTaskStatus.Running;
                
                // Execute a slice
                // We advance time based on how much work the task did
                task.RunSlice(1000); 
                
                // Time accounting (simplified)
                _timerSystem.Advance(10); 
            }
        }
    }
}
