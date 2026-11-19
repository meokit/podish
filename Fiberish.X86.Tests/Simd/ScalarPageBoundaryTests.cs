using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ScalarPageBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void ScalarMultiplyAddAndLoadKeepRoundingAndFaultAtTheLoad(bool loadFirst)
    {
        using var cpu = SimdTestSupport.Create();
        const uint missing = 0x00500000;
        cpu[Reg.EAX] = missing;
        cpu.WriteXmm(0, SimdTestSupport.Lanes(32, 0x3F800001, 1, 2, 3)); // 1 + 2^-23
        cpu.WriteXmm(1, SimdTestSupport.Lanes(32, 0x3F7FFFFE)); // 1 - 2^-23
        cpu.WriteXmm(2, SimdTestSupport.Lanes(32, 0xBF800000)); // -1
        byte[] code =
        [
            0xF3, 0x0F, 0x59, 0xC1, // MULSS xmm0, xmm1
            0xF3, 0x0F, 0x58, 0xC2, // ADDSS xmm0, xmm2
            0xF3, 0x0F, 0x10, 0x18  // MOVSS xmm3, [eax]
        ];
        if (loadFirst)
        {
            cpu[Reg.EBX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, [0, 0, 0, 0x40]);
            code = [0xF3, 0x0F, 0x10, 0x23, .. code]; // MOVSS xmm4, [ebx]
        }
        cpu.WriteCode(code);
        var end = CpuSandbox.CodeAddress + (uint)code.Length;
        cpu.Run(end);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(end - 4, cpu.Eip);
        var rounded = SimdTestSupport.Lanes(32, 0, 1, 2, 3);
        Assert.Equal(rounded, cpu.ReadXmm(0)); // A single FMA would produce -2^-46.

        var count = X86Native.GetBlockCount(cpu.State);
        var blocks = new IntPtr[count];
        fixed (IntPtr* pointer = blocks) Assert.Equal(count, X86Native.GetBlockList(cpu.State, pointer, count));
        var head = Assert.Single(blocks.Where(p => ((X86Native.BasicBlock*)p)->start_eip == CpuSandbox.CodeAddress));
        var block = (X86Native.BasicBlock*)head;
        Assert.Equal(code.Length / 4, block->inst_count);
        if ((X86Native.GetBuildFeatures() & 1) != 0)
            Assert.Contains(loadFirst ? "HandWrittenSuperOpcode_MovssMulssRegAddssMovss"
                    : "HandWrittenSuperOpcode_MulssAddssMovss",
                X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(block->entry)));
        if (loadFirst) Assert.Equal(SimdTestSupport.Lanes(32, 0x40000000), cpu.ReadXmm(4));

        cpu.MapMemory(missing, CpuSandbox.PageSize);
        cpu.WriteMemory(missing, [0, 0, 0x80, 0x3F]);
        cpu.Run(end);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(rounded, cpu.ReadXmm(0));
        Assert.Equal(SimdTestSupport.Lanes(32, 0x3F800000), cpu.ReadXmm(3));
    }

    [Fact]
    public unsafe void FusedScalarStores_FaultingStraddleDoesNotPartiallyWriteOrReplayFirstStore()
    {
        using var cpu = SimdTestSupport.Create();
        const uint edge = CpuSandbox.DataAddress + CpuSandbox.PageSize;
        const uint warmEip = CpuSandbox.CodeAddress + 0x100;
        cpu.MapMemory(edge, CpuSandbox.PageSize);
        cpu.WriteMemory(edge, [0xA1, 0xA2, 0xA3, 0xA4]);
        cpu.ProtectMemory(edge, CpuSandbox.PageSize, 1);
        cpu[Reg.EAX] = edge - 4;
        cpu.WriteMemory(warmEip, [0xF3, 0x0F, 0x11, 0x00]); // MOVSS [eax], xmm0
        byte[] code =
        [
            0xF3, 0x0F, 0x11, 0x00,      // MOVSS [eax], xmm0
            0xF3, 0x0F, 0x11, 0x48, 0x02 // MOVSS [eax+2], xmm1
        ];
        cpu.WriteCode(code);
        cpu.Eip = warmEip;
        cpu.Run(warmEip + 4); // Prime the writable first page's software TLB entry.
        Assert.Equal(EmuStatus.Stopped, cpu.Status);

        cpu.Eip = CpuSandbox.CodeAddress;
        cpu.WriteXmm(0, SimdTestSupport.Lanes(32, 0x44332211));
        cpu.WriteXmm(1, SimdTestSupport.Lanes(32, 0x88776655));
        uint end = CpuSandbox.CodeAddress + (uint)code.Length;
        cpu.Run(end);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(edge, cpu.FaultAddress);
        Assert.Equal(CpuSandbox.CodeAddress + 4, cpu.Eip);
        SimdTestSupport.Bytes([0x11, 0x22, 0x33, 0x44, 0xA1, 0xA2, 0xA3, 0xA4],
            cpu.ReadMemory(edge - 4, 8), "only the first store committed");

        if ((X86Native.GetBuildFeatures() & 1) != 0)
        {
            var count = X86Native.GetBlockCount(cpu.State);
            var blocks = new IntPtr[count];
            fixed (IntPtr* pointer = blocks) Assert.Equal(count, X86Native.GetBlockList(cpu.State, pointer, count));
            var head = Assert.Single(blocks.Where(p => ((X86Native.BasicBlock*)p)->start_eip == CpuSandbox.CodeAddress));
            Assert.Contains("HandWrittenSuperOpcode_StoreTwoMovss",
                X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(((X86Native.BasicBlock*)head)->entry)));
        }

        cpu.ProtectMemory(edge, CpuSandbox.PageSize, 3);
        cpu.WriteMemory(edge - 4, [0xB1, 0xB2]); // Detect replay of the already-completed first store.
        cpu.Run(end);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        SimdTestSupport.Bytes([0xB1, 0xB2, 0x55, 0x66, 0x77, 0x88, 0xA3, 0xA4],
            cpu.ReadMemory(edge - 4, 8), "resume committed only the second store");
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void ScalarMemoryMove_AtPageEndDoesNotAccessFullXmmWidth(int width, bool store)
    {
        using var cpu = SimdTestSupport.Create();
        uint edge = CpuSandbox.DataAddress + 0x1000;
        cpu[Reg.EAX] = edge - (uint)width;
        byte[] source = SimdTestSupport.Lanes(64, 0x7FF8123456789ABC, 0xFEDCBA9876543210);
        cpu.WriteXmm(0, source);
        cpu.WriteMemory(edge - 16, Enumerable.Repeat((byte)0xA5, 16).ToArray());
        if (!store) cpu.WriteMemory(cpu[Reg.EAX], source.AsSpan(0, width));
        var expected = new byte[16];
        source.AsSpan(0, width).CopyTo(expected);
        byte[] code = SimdTestSupport.Instruction(width == 4 ? (byte)0xF3 : (byte)0xF2, store ? (byte)0x11 : (byte)0x10, true);
        SimdTestSupport.Execute(cpu, code, store ? null : expected,
            detail: $"scalar page edge width={width * 8} store={store} address={cpu[Reg.EAX]:X8}");
        byte[] memory = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        source.AsSpan(0, width).CopyTo(memory.AsSpan(16 - width));
        SimdTestSupport.Bytes(memory, cpu.ReadMemory(edge - 16, 16),
            $"bytes={Convert.ToHexString(code)} scalar page edge width={width * 8} store={store}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ScalarCvtt_AtPageEndReadsOnlyTheScalarOperand(int width)
    {
        using var cpu = SimdTestSupport.Create();
        uint address = CpuSandbox.DataAddress + 0x1000 - (uint)width;
        cpu[Reg.EAX] = address;
        byte[] source = width == 4 ? SimdTestSupport.Lanes(32, BitConverter.SingleToUInt32Bits(-1.75f))
            : SimdTestSupport.Lanes(64, BitConverter.DoubleToUInt64Bits(-1.75));
        cpu.WriteMemory(address, source.AsSpan(0, width));
        byte[] code = { width == 4 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C, 0x10 };
        SimdTestSupport.Execute(cpu, code, registerDestination: Reg.EDX, registerValue: 0xFFFFFFFF,
            detail: $"cvtt page edge width={width * 8} address={address:X8} input=-1.75");
    }
}
