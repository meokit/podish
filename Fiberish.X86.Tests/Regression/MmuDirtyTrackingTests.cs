using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

/// <summary>
/// A clean writable page must not become write-fast-path eligible merely
/// because it was read: the first store has to go through the slow path so the
/// Dirty bit is recorded for writeback/eviction.
/// </summary>
public sealed class MmuDirtyTrackingTests
{
    [Fact]
    public void StoreAfterRead_MarksPageDirty()
    {
        using var cpu = new CpuSandbox();
        uint address = CpuSandbox.DataAddress + 0x40;

        Assert.Equal(0, X86Native.IsDirty(cpu.State, address));

        cpu.WriteCode(
        [
            0x8A, 0x05, 0x40, 0x00, 0x20, 0x00, // mov al, [0x00200040]
            0x88, 0x05, 0x40, 0x00, 0x20, 0x00  // mov [0x00200040], al
        ]);
        cpu.Run(CpuSandbox.CodeAddress + 12);

        Assert.Equal(1, X86Native.IsDirty(cpu.State, address));
        Assert.Equal((byte)0x00, cpu.ReadMemory(address, 1)[0]);
    }
}
