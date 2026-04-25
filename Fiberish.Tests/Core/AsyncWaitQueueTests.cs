using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class AsyncWaitQueueTests
{
    [Fact]
    public void RegisterCancelable_DisposeBeforeSignal_DoesNotInvokeContinuation()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(100);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var fired = 0;
        var task = new FiberTask(101, process, new MockEngine(), scheduler);

        void Entry()
        {
            var reg = queue.RegisterCancelable(() => fired++, task);
            Assert.NotNull(reg);
            reg!.Dispose();

            queue.Signal();

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void RegisterCancelable_DisposeOneOfMultiple_OnlyActiveContinuationRuns()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(200);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var firedA = 0;
        var firedB = 0;
        var task = new FiberTask(201, process, new MockEngine(), scheduler);

        void Entry()
        {
            var regA = queue.RegisterCancelable(() => firedA++, task);
            var regB = queue.RegisterCancelable(() => firedB++, task);

            Assert.NotNull(regA);
            Assert.NotNull(regB);

            regA!.Dispose();
            queue.Signal();

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(0, firedA);
        Assert.Equal(1, firedB);
    }

    [Fact]
    public void WaitQueueAwaiter_PendingSignal_StillSchedulesContinuation()
    {
        var scheduler = new KernelScheduler();
        
        var process = TestRuntimeFactory.CreateProcess(300);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var resumed = false;
        var task = new FiberTask(301, process, new MockEngine(), scheduler);

        void Entry()
        {
            var awaiter = queue.WaitAsync(task).GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                resumed = true;
                task.Exited = true;
                task.Status = FiberTaskStatus.Terminated;
            });

            task.PostSignal((int)Signal.SIGUSR1);
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.True(resumed);
    }

    [Fact]
    public void WaitQueueAwaiter_QueueSignaledRace_WithPendingSignal_MustCompleteAsEvent()
    {
        var scheduler = new KernelScheduler();
        
        var process = TestRuntimeFactory.CreateProcess(400);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var result = AwaitResult.Interrupted;
        var resumed = false;
        var task = new FiberTask(401, process, new MockEngine(), scheduler);

        void Entry()
        {
            // Simulate race window:
            // 1) caller observed IsCompleted=false
            // 2) queue becomes signaled just before OnCompleted registration.
            queue.Signal();
            task.PostSignal((int)Signal.SIGUSR1);

            var awaiter = queue.WaitAsync(task).GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                resumed = true;
                result = awaiter.GetResult();
                task.Exited = true;
                task.Status = FiberTaskStatus.Terminated;
            });
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.True(resumed);
        Assert.Equal(AwaitResult.Completed, result);
    }

    [Fact]
    public void AsyncWaitQueue_BoundQueue_NonSchedulerThreadMutation_Throws()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(500);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var task = new FiberTask(501, process, new MockEngine(), scheduler);
        Exception? captured = null;

        void Entry()
        {
            using var reg = queue.RegisterCancelable(() => { }, task);
            queue.Signal();

            using var done = new ManualResetEventSlim(false);
            _ = Task.Run(() =>
            {
                try
                {
                    queue.Reset();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            Assert.True(done.Wait(1000), "Background queue mutation did not complete in time.");

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
    }

    [Fact]
    public void Reset_MustNotDropAlreadyRegisteredWaiters()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(600);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var fired = 0;
        var task = new FiberTask(601, process, new MockEngine(), scheduler);

        void Entry()
        {
            using var reg = queue.RegisterCancelable(() => fired++, task);

            queue.Reset();
            queue.Signal();

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void QueueReadinessRegistration_OnNextSignal_RacedReadableState_MustScheduleImmediately()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(700);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var fired = 0;
        var registered = false;
        var firstProbe = true;
        var task = new FiberTask(701, process, new MockEngine(), scheduler);

        void Entry()
        {
            if (registered)
                return;

            registered = true;
            var watch = new QueueReadinessWatch(
                LinuxConstants.POLLIN,
                () =>
                {
                    if (firstProbe)
                    {
                        firstProbe = false;
                        queue.Signal();
                        return false;
                    }

                    return true;
                },
                queue,
                queue.Reset);

            var registration = QueueReadinessRegistration.RegisterHandleOnNextSignal(
                () =>
                {
                    fired++;
                    task.Exited = true;
                    task.Status = FiberTaskStatus.Terminated;
                },
                scheduler,
                LinuxConstants.POLLIN,
                watch);

            Assert.NotNull(registration);
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void RegisterCancelableOnNextSignal_AlreadySignaled_WaitsForFreshSignal()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(800);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var fired = 0;
        var task = new FiberTask(801, process, new MockEngine(), scheduler);

        void Entry()
        {
            queue.Signal();

            var registration = queue.RegisterCancelableOnNextSignal(() =>
            {
                fired++;
                task.Exited = true;
                task.Status = FiberTaskStatus.Terminated;
            }, task);

            Assert.NotNull(registration);
            Assert.Equal(0, fired);

            queue.Signal();
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void RegisterCancelableOnNextSignal_RepeatedSignalsWithoutReset_WakesEachGeneration()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(900);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue(scheduler);
        var fired = 0;
        var phase = 0;
        var task = new FiberTask(901, process, new MockEngine(), scheduler);

        void Entry()
        {
            if (phase == 0)
            {
                phase = 1;
                var registration = queue.RegisterCancelableOnNextSignal(() =>
                {
                    fired++;
                    phase = 2;

                    var secondRegistration = queue.RegisterCancelableOnNextSignal(() =>
                    {
                        fired++;
                        task.Exited = true;
                        task.Status = FiberTaskStatus.Terminated;
                    }, task);

                    Assert.NotNull(secondRegistration);
                    queue.Signal();
                }, task);

                Assert.NotNull(registration);
                queue.Signal();
            }
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(2, fired);
        Assert.Equal(2, phase);
    }

    private sealed class MockEngine : Engine
    {
        public MockEngine() : base(true)
        {
        }

        public override EmuStatus Status => EmuStatus.Running;
        public override uint Eip { get; set; }
        public override uint Eflags { get; set; }

        protected override void Dispose(bool disposing)
        {
        }

        public override void Run(uint endEip = 0, ulong maxInsts = 0)
        {
        }

        public override uint RegRead(Reg reg)
        {
            return 0;
        }

        public override void RegWrite(Reg reg, uint val)
        {
        }
    }
}
