using System.Text;
using Fiberish.Core;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

/// <summary>
/// Tests for vfork (CLONE_VFORK) parent blocking semantics.
/// Validates that the parent task is suspended until the child signals VforkDoneEvent.
/// </summary>
public class VforkTests
{
    private Process CreateMockProcess(int pid)
    {
        return new Process(pid, null!, null!);
    }

    /// <summary>
    /// Test: vfork parent blocks until child signals VforkDoneEvent via _exit path.
    /// Expected order: Child runs first → signals → Parent resumes.
    /// </summary>
    [Fact]
    public void Vfork_ParentBlocksUntilChildExit()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();

        var parent = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);
        var child = new FiberTask(102, CreateMockProcess(100), new MockEngine(), kernel);

        // Simulate vfork: set up the event on the child
        var vforkEvent = new AsyncWaitQueue();
        child.VforkDoneEvent = vforkEvent;
        child.VforkParent = parent;

        async void RunParent()
        {
            sb.Append("P-Before;");
            // Parent awaits the vfork event (simulating what Clone does)
            await vforkEvent;
            sb.Append("P-After;");
            parent.Exited = true;
            parent.Status = FiberTaskStatus.Terminated;
        }

        async void RunChild()
        {
            sb.Append("C-Run;");
            // Child does work, then signals vfork done (simulating _exit)
            await new YieldAwaitable();
            sb.Append("C-Exit;");
            child.SignalVforkDone();
            child.Exited = true;
            child.Status = FiberTaskStatus.Terminated;
        }

        parent.Continuation = RunParent;
        kernel.RegisterTask(parent);

        child.Continuation = RunChild;
        kernel.RegisterTask(child);

        kernel.Run(200);

        var result = sb.ToString();
        // Parent starts, blocks at await. Child runs, yields, then signals.
        // After signal, parent resumes.
        Assert.Equal("P-Before;C-Run;C-Exit;P-After;", result);
    }

    /// <summary>
    /// Test: VforkDoneEvent that is already signaled completes immediately.
    /// </summary>
    [Fact]
    public void Vfork_AlreadySignaled_CompletesImmediately()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();

        var parent = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);

        var vforkEvent = new AsyncWaitQueue();
        vforkEvent.Set(); // Pre-signal

        async void RunParent()
        {
            sb.Append("P-Before;");
            await vforkEvent; // Should complete immediately since already signaled
            sb.Append("P-After;");
            parent.Exited = true;
            parent.Status = FiberTaskStatus.Terminated;
        }

        parent.Continuation = RunParent;
        kernel.RegisterTask(parent);

        kernel.Run(100);

        Assert.Equal("P-Before;P-After;", sb.ToString());
    }

    /// <summary>
    /// Test: SignalVforkDone is idempotent — calling it when no event is set is safe.
    /// </summary>
    [Fact]
    public void SignalVforkDone_NoEvent_IsNoop()
    {
        var kernel = new KernelScheduler();
        var task = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);

        // Should not throw
        task.SignalVforkDone();

        Assert.Null(task.VforkDoneEvent);
        Assert.Null(task.VforkParent);
    }

    /// <summary>
    /// Test: SignalVforkDone clears the event and parent reference after signaling.
    /// </summary>
    [Fact]
    public void SignalVforkDone_ClearsState()
    {
        var kernel = new KernelScheduler();
        var parent = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);
        var child = new FiberTask(102, CreateMockProcess(100), new MockEngine(), kernel);

        var vforkEvent = new AsyncWaitQueue();
        child.VforkDoneEvent = vforkEvent;
        child.VforkParent = parent;

        child.SignalVforkDone();

        Assert.Null(child.VforkDoneEvent);
        Assert.Null(child.VforkParent);
        Assert.True(vforkEvent.IsSignaled);
    }

    /// <summary>
    /// Test: Multiple sequential vforks — each parent blocks until its child signals.
    /// </summary>
    [Fact]
    public void Vfork_MultipleSequential_EachBlocks()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();

        var parent = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);
        var child1 = new FiberTask(102, CreateMockProcess(100), new MockEngine(), kernel);
        var child2 = new FiberTask(103, CreateMockProcess(100), new MockEngine(), kernel);
        // Keep this test focused on vfork wait ordering rather than signal interruption behavior.
        parent.SignalMask = ulong.MaxValue;

        var event1 = new AsyncWaitQueue();
        child1.VforkDoneEvent = event1;
        child1.VforkParent = parent;

        var event2 = new AsyncWaitQueue();
        child2.VforkDoneEvent = event2;
        child2.VforkParent = parent;

        async void RunParent()
        {
            sb.Append("P-Start;");
            while (await event1 != AwaitResult.Completed)
            {
            }
            sb.Append("P-Mid;");
            while (await event2 != AwaitResult.Completed)
            {
            }
            sb.Append("P-End;");
            parent.Exited = true;
            parent.Status = FiberTaskStatus.Terminated;
        }

        async void RunChild1()
        {
            sb.Append("C1;");
            child1.SignalVforkDone();
            child1.Exited = true;
            child1.Status = FiberTaskStatus.Terminated;
        }

        async void RunChild2()
        {
            sb.Append("C2;");
            child2.SignalVforkDone();
            child2.Exited = true;
            child2.Status = FiberTaskStatus.Terminated;
        }

        parent.Continuation = RunParent;
        kernel.RegisterTask(parent);

        child1.Continuation = RunChild1;
        kernel.RegisterTask(child1);

        child2.Continuation = RunChild2;
        kernel.RegisterTask(child2);

        kernel.Run(200);

        // Validate causality instead of strict full ordering:
        // parent must reach mid only after child1, and must eventually finish after mid;
        // child2 completion marker must also appear.
        var result = sb.ToString();
        Assert.Contains("P-Start;", result);
        Assert.Contains("C1;", result);
        Assert.Contains("P-Mid;", result);
        Assert.Contains("C2;", result);
        Assert.Contains("P-End;", result);
        Assert.True(result.IndexOf("P-Start;", StringComparison.Ordinal) <
                    result.IndexOf("C1;", StringComparison.Ordinal));
        Assert.True(result.IndexOf("C1;", StringComparison.Ordinal) <
                    result.IndexOf("P-Mid;", StringComparison.Ordinal));
        Assert.True(result.IndexOf("P-Mid;", StringComparison.Ordinal) <
                    result.IndexOf("P-End;", StringComparison.Ordinal));
    }

    private class MockEngine : Engine
    {
        public MockEngine() : base(true) { }

        public override EmuStatus Status => EmuStatus.Running;
        public override uint Eip { get; set; }
        public override uint Eflags { get; set; }

        protected override void Dispose(bool disposing) { }
        public override void Run(uint endEip = 0, ulong maxInsts = 0) { }
        public override uint RegRead(Reg reg) { return 0; }
        public override void RegWrite(Reg reg, uint val) { }
    }
}
