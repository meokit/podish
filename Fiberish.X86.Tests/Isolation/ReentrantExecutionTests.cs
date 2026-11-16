using System.Runtime.InteropServices;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class ReentrantExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void FaultCallbackRejectsPeerExecutionWithoutChangingOuterFloatingEnvironment(bool run)
    {
        using var outer = FloatingStateIsolationTests.CreateRounder(1, 1.5);
        using var inner = FloatingStateIsolationTests.CreateRounder(2, 1.5);
        outer.UnmapMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize);
        var context = new FaultContext(outer, inner);
        var root = GCHandle.Alloc(context);
        try
        {
            X86Native.SetFaultCallback(outer.State, &OnFault, GCHandle.ToIntPtr(root));
            FloatingStateIsolationTests.ExecuteRounder(outer, run);
            Assert.Null(context.Error);
            Assert.Equal(1, context.Invocations);
            Assert.True(context.PeerRunRejected);
            Assert.Equal(CpuSandbox.CodeAddress, inner.Eip);
            Assert.Equal(1.0, FloatingStateIsolationTests.ReadResult(outer));
            FloatingStateIsolationTests.ExecuteRounder(inner, run: true);
            Assert.Equal(2.0, FloatingStateIsolationTests.ReadResult(inner));
            Assert.Equal(-1, outer.FaultVector);
        }
        finally
        {
            X86Native.SetFaultCallback(outer.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnFault(IntPtr state, uint address, int isWrite, IntPtr userdata)
    {
        var context = (FaultContext)GCHandle.FromIntPtr(userdata).Target!;
        try
        {
            if (context.Invocations++ != 0 || state != context.Outer.State ||
                address != CpuSandbox.DataAddress || isWrite != 0)
                throw new InvalidOperationException($"Unexpected nested fault at {address:X8} (write={isWrite}).");
            try { context.Inner.Run(CpuSandbox.CodeAddress + 14); }
            catch (InvalidOperationException) { context.PeerRunRejected = true; }
            context.Outer.MapMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize);
            context.Outer.WriteMemory(CpuSandbox.DataAddress, BitConverter.GetBytes(1.5));
            return 1;
        }
        catch (Exception error)
        {
            // Never unwind a managed exception through native dispatch frames.
            context.Error = error;
            X86Native.EmuFault(state);
            return 0;
        }
    }

    private sealed class FaultContext(CpuSandbox outer, CpuSandbox inner)
    {
        public CpuSandbox Outer { get; } = outer;
        public CpuSandbox Inner { get; } = inner;
        public int Invocations { get; set; }
        public bool PeerRunRejected { get; set; }
        public Exception? Error { get; set; }
    }
}
