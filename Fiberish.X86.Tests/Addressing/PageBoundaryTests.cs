using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class InstructionFetchBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstructionEndingAtPageBoundary_DoesNotFetchFollowingUnmappedPage(bool immediate)
    {
        using var cpu = AddressingTestSupport.Create();
        byte[] code = immediate ? new byte[] { 0xB8, 0xEF, 0xCD, 0xAB, 0x89 } : new byte[] { 0x90 };
        cpu.Eip = CpuSandbox.CodeAddress + 0x1000 - (uint)code.Length;
        cpu.WriteMemory(cpu.Eip, code);
        (Reg Register, uint Value)[] changes = immediate ? new[] { (Reg.EAX, 0x89ABCDEFu) } : Array.Empty<(Reg, uint)>();
        AddressingTestSupport.Step(cpu, code, $"fetch ending at page edge instructionLength={code.Length}",
            CpuSandbox.CodeAddress + 0x1000, changes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InstructionSpanningMappedPages_ReadsOnlyItsBytes(int bytesOnFirstPage)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.MapMemory(CpuSandbox.CodeAddress + 0x1000, 0x1000, 7);
        byte[] code = { 0xB8, 0xEF, 0xCD, 0xAB, 0x89 };
        cpu.Eip = CpuSandbox.CodeAddress + 0x1000 - (uint)bytesOnFirstPage;
        cpu.WriteMemory(cpu.Eip, code);
        AddressingTestSupport.Step(cpu, code, $"split instruction bytesOnFirstPage={bytesOnFirstPage}",
            null, (Reg.EAX, 0x89ABCDEF));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TruncatedInstruction_FaultsOnFirstMissingFetchByteWithoutChangingState(int bytesOnFirstPage)
    {
        using var cpu = AddressingTestSupport.Create();
        byte[] code = { 0xB8, 0xEF, 0xCD, 0xAB, 0x89 };
        cpu.Eip = CpuSandbox.CodeAddress + 0x1000 - (uint)bytesOnFirstPage;
        cpu.WriteMemory(cpu.Eip, code.AsSpan(0, bytesOnFirstPage));
        AddressingTestSupport.Fault(cpu, code, 14, CpuSandbox.CodeAddress + 0x1000,
            $"instruction fetch #PF bytesPresent={bytesOnFirstPage} fullLength={code.Length}");
    }

    [Fact]
    public void EntirelyUnmappedInstructionFetch_RetainsEipAndRegisters()
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.Eip = CpuSandbox.CodeAddress + 0x1000;
        AddressingTestSupport.Fault(cpu, Array.Empty<byte>(), 14, cpu.Eip, "unmapped instruction fetch");
    }
}

public sealed class DataPageBoundaryTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void AccessEndingAtPageBoundary_DoesNotTouchNextPage(int width, bool store)
    {
        using var cpu = AddressingTestSupport.Create();
        uint edge = CpuSandbox.DataAddress + 0x1000;
        cpu[Reg.EBX] = edge - (uint)width;
        cpu[Reg.EDX] = 0x89ABCDEF;
        byte[] expected = Enumerable.Repeat((byte)0xA5, 8).ToArray();
        cpu.WriteMemory(edge - 8, expected);
        byte[] value = AddressingTestSupport.Word(0x12345678);
        if (!store)
        {
            cpu.WriteMemory(cpu[Reg.EBX], value.AsSpan(0, width));
            value.AsSpan(0, width).CopyTo(expected.AsSpan(8 - width));
        }
        else AddressingTestSupport.Word(cpu[Reg.EDX]).AsSpan(0, width).CopyTo(expected.AsSpan(8 - width));
        byte[] code = Move(width, store);
        uint mask = width == 1 ? 0xFFu : width == 2 ? 0xFFFFu : uint.MaxValue;
        uint result = (cpu[Reg.EDX] & ~mask) | (0x12345678 & mask);
        AddressingTestSupport.Execute(cpu, code, $"data page edge width={width * 8} store={store} address={cpu[Reg.EBX]:X8}",
            store ? Array.Empty<(Reg, uint)>() : new[] { (Reg.EDX, result) });
        AddressingTestSupport.Bytes(expected, cpu.ReadMemory(edge - 8, 8),
            $"bytes={Convert.ToHexString(code)} width={width * 8} store={store} page-edge memory");
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public void CrossPageMappedAccess_PreservesLittleEndianOrderAndNeighbors(int width, int bytesOnFirstPage)
    {
        foreach (bool store in new[] { false, true })
        {
            using var cpu = AddressingTestSupport.Create();
            uint edge = CpuSandbox.DataAddress + 0x1000;
            cpu.MapMemory(edge, 0x1000);
            byte[] expected = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            cpu.WriteMemory(edge - 8, expected);
            cpu[Reg.EBX] = edge - (uint)bytesOnFirstPage;
            cpu[Reg.EDX] = 0x89ABCDEF;
            int offset = 8 - bytesOnFirstPage;
            uint loaded = 0;
            for (int i = 0; i < width; i++) loaded |= (uint)expected[offset + i] << (i * 8);
            uint result = width == 2 ? (cpu[Reg.EDX] & 0xFFFF0000) | loaded : loaded;
            if (store) AddressingTestSupport.Word(cpu[Reg.EDX]).AsSpan(0, width).CopyTo(expected.AsSpan(offset));
            byte[] code = Move(width, store);
            AddressingTestSupport.Execute(cpu, code,
                $"mapped cross-page width={width * 8} firstPageBytes={bytesOnFirstPage} store={store}",
                store ? Array.Empty<(Reg, uint)>() : new[] { (Reg.EDX, result) });
            AddressingTestSupport.Bytes(expected, cpu.ReadMemory(edge - 8, 16),
                $"bytes={Convert.ToHexString(code)} width={width * 8} split={bytesOnFirstPage} store={store}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossPageFault_RetainsInstructionStateAndDoesNotPartiallyStore(bool store, bool protectionFault)
    {
        using var cpu = AddressingTestSupport.Create();
        uint edge = CpuSandbox.DataAddress + 0x1000;
        cpu[Reg.EBX] = edge - 2;
        cpu[Reg.EDX] = 0x89ABCDEF;
        byte[] before = { 0x11, 0x22, 0x33, 0x44 };
        cpu.WriteMemory(edge - 4, before);
        byte[] secondPage = { 0x55, 0x66, 0x77, 0x88 };
        if (protectionFault)
        {
            cpu.MapMemory(edge, 0x1000);
            cpu.WriteMemory(edge, secondPage);
            cpu.ProtectMemory(edge, 0x1000, store ? (byte)1 : (byte)2);
        }
        byte[] code = Move(4, store);
        cpu.WriteCode(code);
        AddressingTestSupport.Fault(cpu, code, 14, edge,
            $"cross-page #PF width=32 firstPageBytes=2 store={store} protection={protectionFault}");
        AddressingTestSupport.Bytes(before, cpu.ReadMemory(edge - 4, 4),
            $"bytes={Convert.ToHexString(code)} faulting cross-page access must not partly write first page");
        if (protectionFault)
        {
            cpu.ProtectMemory(edge, 0x1000, 3);
            AddressingTestSupport.Bytes(secondPage, cpu.ReadMemory(edge, 4),
                $"bytes={Convert.ToHexString(code)} faulting access must not modify second page");
        }
    }

    private static byte[] Move(int width, bool store) => width == 2
        ? new byte[] { 0x66, store ? (byte)0x89 : (byte)0x8B, 0x13 }
        : new byte[] { width == 1 ? (store ? (byte)0x88 : (byte)0x8A) : (store ? (byte)0x89 : (byte)0x8B), 0x13 };
}
