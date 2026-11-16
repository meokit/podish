using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class CpuLifecycleTests
{
    [Fact]
    public void NativeCreate_HasArchitecturalResetState()
    {
        using var cpu = new CpuSandbox(false);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(-1, cpu.FaultVector);
        Assert.Equal(0u, cpu.Eip);
        Assert.Equal(0x202u, cpu.Eflags);
        Assert.Equal(0x037F, cpu.Fcw);
        Assert.Equal(0, cpu.Fsw);
        Assert.Equal(0xFFFF, cpu.Ftw);
        Assert.Equal(0x1F80u, cpu.Mxcsr);
        foreach (var register in Enum.GetValues<Reg>()) Assert.Equal(0u, cpu[register]);
    }

    [Fact]
    public void IndependentInstances_SameGuestAddressesHaveIndependentMemoryAndCode()
    {
        using var first = new CpuSandbox();
        using var second = new CpuSandbox();
        first.WriteCode([0xB8, 0x11, 0x11, 0x11, 0x11]);
        second.WriteCode([0xB8, 0x22, 0x22, 0x22, 0x22]);
        first.WriteMemory(CpuSandbox.DataAddress, [0xAA]);
        second.WriteMemory(CpuSandbox.DataAddress, [0x55]);
        Assert.NotEqual(EmuStatus.Fault, first.Step());
        Assert.NotEqual(EmuStatus.Fault, second.Step());
        Assert.Equal(0x11111111u, first[Reg.EAX]);
        Assert.Equal(0x22222222u, second[Reg.EAX]);
        Assert.Equal(new byte[] { 0xAA }, first.ReadMemory(CpuSandbox.DataAddress, 1));
        Assert.Equal(new byte[] { 0x55 }, second.ReadMemory(CpuSandbox.DataAddress, 1));
    }

    [Fact]
    public void DisposedSandboxRejectsAccessAndNewSandboxDoesNotInheritState()
    {
        var previous = new CpuSandbox();
        previous[Reg.EAX] = uint.MaxValue;
        previous.Mxcsr = 0x7F80;
        previous.Fcw = 0x0F7F;
        previous.WriteMemory(CpuSandbox.DataAddress, [0xAA, 0x55]);
        previous.Dispose();
        previous.Dispose();
        Assert.Throws<ObjectDisposedException>(() => previous.Eip);
        using var next = new CpuSandbox();
        Assert.Equal(0u, next[Reg.EAX]);
        Assert.Equal(0x1F80u, next.Mxcsr);
        Assert.Equal(0x037F, next.Fcw);
        Assert.Equal(new byte[2], next.ReadMemory(CpuSandbox.DataAddress, 2));
    }

    [Fact]
    public void GuestFault_DoesNotContaminatePeerOrNextExecution()
    {
        using var broken = new CpuSandbox();
        using var healthy = new CpuSandbox();
        broken.WriteCode([0x0F, 0x0B]); // UD2
        Assert.Equal(EmuStatus.Fault, broken.Step());
        Assert.Equal(6, broken.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress, broken.Eip);
        healthy.WriteCode([0x90]);
        Assert.NotEqual(EmuStatus.Fault, healthy.Step());
        Assert.Equal(-1, healthy.FaultVector);
        broken.WriteCode([0x90]);
        Assert.NotEqual(EmuStatus.Fault, broken.Step());
        Assert.Equal(-1, broken.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress + 1, broken.Eip);
    }

    [Fact]
    public void GuestTsc_IsDeterministicAndInstanceOwned()
    {
        using var first = new CpuSandbox();
        using var second = new CpuSandbox();
        first.WriteCode([0x0F, 0x31, 0x0F, 0x31]);
        second.WriteCode([0x0F, 0x31]);
        first.Step();
        Assert.Equal(0u, first[Reg.EAX]);
        first.Step();
        Assert.Equal(1000u, first[Reg.EAX]);
        second.Step();
        Assert.Equal(0u, second[Reg.EAX]);
        Assert.Equal(0u, second[Reg.EDX]);
    }

    [Fact]
    public void ExternalCodeRewriteInvalidatesOnlyItsOwnWarmBlocks()
    {
        using var first = new CpuSandbox();
        using var peer = new CpuSandbox();
        first.WriteCode([0xB8, 1, 0, 0, 0]);
        peer.WriteCode([0xB8, 7, 0, 0, 0]);
        first.Run(CpuSandbox.CodeAddress + 5);
        peer.Run(CpuSandbox.CodeAddress + 5);
        first.WriteCode([0xB8, 2, 0, 0, 0]);
        first.Run(CpuSandbox.CodeAddress + 5);
        peer.Eip = CpuSandbox.CodeAddress;
        peer.Run(CpuSandbox.CodeAddress + 5);
        Assert.Equal(2u, first[Reg.EAX]);
        Assert.Equal(7u, peer[Reg.EAX]);
    }
}

public sealed class FloatingStateIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameThread_ABA_RoundingAndExceptionStateBelongsToEachEngine(bool run)
    {
        using var up = CreateRounder(2, 1.5);
        using var down = CreateRounder(1, 1.5);
        ExecuteRounder(up, run);
        ExecuteRounder(down, run);
        Assert.Equal(2.0, ReadResult(up));
        Assert.Equal(1.0, ReadResult(down));

        // Re-enter A after B changed TLS precision and rounding. A keeps its own
        // architectural sticky flags but must not inherit B's rounding mode.
        up.Eip = CpuSandbox.CodeAddress;
        ExecuteRounder(up, run);
        Assert.Equal(2.0, ReadResult(up));
        Assert.Equal(0x0B7F, up.Fcw);
        Assert.Equal(0x077F, down.Fcw);
        Assert.Equal(0, up.Fsw & 1);
        Assert.Equal(0, down.Fsw & 1);
    }

    [Fact]
    public void DifferentMxcsrAndXmmStates_DoNotCrossEngines()
    {
        using var first = new CpuSandbox();
        using var second = new CpuSandbox();
        first.Mxcsr = 0x3F80;
        second.Mxcsr = 0x5F80;
        first.WriteXmm(0, Enumerable.Repeat((byte)0x55, 16).ToArray());
        second.WriteXmm(0, Enumerable.Repeat((byte)0xAA, 16).ToArray());
        first.WriteCode([0x66, 0x0F, 0xEF, 0xC0]); // PXOR XMM0,XMM0
        second.WriteCode([0x90]);
        first.Step();
        second.Step();
        Assert.Equal(new byte[16], first.ReadXmm(0));
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 16).ToArray(), second.ReadXmm(0));
        Assert.Equal(0x3F80u, first.Mxcsr);
        Assert.Equal(0x5F80u, second.Mxcsr);
    }

    internal static CpuSandbox CreateRounder(int roundingMode, double value)
    {
        var cpu = new CpuSandbox();
        try
        {
            cpu.Fcw = (ushort)(0x037F | roundingMode << 10);
            byte[] code = [0xDD, 0x05, 0, 0, 0, 0, 0xD9, 0xFC, 0xDD, 0x1D, 0, 0, 0, 0];
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(2), CpuSandbox.DataAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(10), CpuSandbox.DataAddress + 8);
            cpu.WriteCode(code);
            cpu.WriteMemory(CpuSandbox.DataAddress, BitConverter.GetBytes(value));
            return cpu;
        }
        catch
        {
            cpu.Dispose();
            throw;
        }
    }

    internal static void ExecuteRounder(CpuSandbox cpu, bool run)
    {
        if (run) cpu.Run(CpuSandbox.CodeAddress + 14);
        else for (var i = 0; i < 3; i++) cpu.Step();
        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress + 14, cpu.Eip);
    }

    internal static double ReadResult(CpuSandbox cpu) => BitConverter.ToDouble(cpu.ReadMemory(CpuSandbox.DataAddress + 8, 8));
}

public sealed class ConcurrentEngineTests
{
    [Fact]
    public async Task BoundedParallelEngines_MatchSerialResultsAndReleaseOnFailure()
    {
        var expected = Enumerable.Range(0, 64).Select(Execute).ToArray();
        var actual = new uint[expected.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, expected.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4 }, (index, _) =>
            {
                actual[index] = Execute(index);
                return ValueTask.CompletedTask;
            });
        Assert.Equal(expected, actual);
        using var after = new CpuSandbox();
        Assert.Equal(0u, after[Reg.EAX]);
        Assert.Equal(new byte[16], after.ReadXmm(0));
    }

    private static uint Execute(int index)
    {
        using var cpu = FloatingStateIsolationTests.CreateRounder(index % 4, index % 2 == 0 ? 1.5 : -1.5);
        FloatingStateIsolationTests.ExecuteRounder(cpu, index % 2 == 0);
        var expected = (index % 4) switch { 0 => 2.0, 1 => -2.0, 2 => 2.0, _ => -1.0 };
        Assert.Equal(expected, FloatingStateIsolationTests.ReadResult(cpu));
        return cpu.Fcw;
    }
}
