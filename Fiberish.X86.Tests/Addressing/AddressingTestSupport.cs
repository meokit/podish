using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

internal static class AddressingTestSupport
{
    public static CpuSandbox Create()
    {
        var cpu = new CpuSandbox();
        for (int i = 0; i < 8; i++) cpu[(Reg)i] = 0x12340000u + (uint)i * 0x101u;
        cpu[Reg.ESP] = CpuSandbox.StackAddress + 0x800;
        cpu.Eflags = 0xAD7;
        return cpu;
    }

    public static byte[] Word(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] WithDisplacement(byte[] prefix, uint displacement) => prefix.Concat(Word(displacement)).ToArray();

    public static void Execute(CpuSandbox cpu, byte[] code, string detail, params (Reg Register, uint Value)[] changes)
    {
        cpu.WriteCode(code);
        Step(cpu, code, detail, null, changes);
    }

    public static void Step(CpuSandbox cpu, byte[] code, string detail, uint? nextEip = null,
        params (Reg Register, uint Value)[] changes)
    {
        uint eip = cpu.Eip, flags = cpu.Eflags;
        uint[] before = Registers(cpu);
        string context = Context(code, detail, before);
        Assert.True(cpu.Step() == EmuStatus.Running,
            $"{context}; status={cpu.Status} vector={cpu.FaultVector} fault={cpu.FaultAddress:X8}");
        uint expectedEip = nextEip ?? eip + (uint)code.Length;
        Assert.True(cpu.Eip == expectedEip, $"{context}; EIP expected={expectedEip:X8} actual={cpu.Eip:X8}");
        Assert.True(cpu.Eflags == flags, $"{context}; EFLAGS expected={flags:X8} actual={cpu.Eflags:X8}");
        foreach (var change in changes) before[(int)change.Register] = change.Value;
        CheckRegisters(cpu, before, context);
    }

    public static void Fault(CpuSandbox cpu, byte[] code, int vector, uint? address, string detail)
    {
        uint eip = cpu.Eip, flags = cpu.Eflags;
        uint[] before = Registers(cpu);
        string context = Context(code, detail, before);
        Assert.True(cpu.Step() == EmuStatus.Fault, $"{context}; expected fault #{vector}, status={cpu.Status}");
        Assert.True(cpu.FaultVector == vector, $"{context}; vector expected={vector} actual={cpu.FaultVector}");
        if (address.HasValue)
            Assert.True(cpu.FaultAddress == address.Value,
                $"{context}; fault address expected={address:X8} actual={cpu.FaultAddress:X8}");
        Assert.True(cpu.Eip == eip, $"{context}; fault must retain instruction EIP={eip:X8}, actual={cpu.Eip:X8}");
        Assert.True(cpu.Eflags == flags, $"{context}; fault changed EFLAGS {flags:X8}->{cpu.Eflags:X8}");
        CheckRegisters(cpu, before, context);
    }

    public static void Bytes(byte[] expected, byte[] actual, string detail) =>
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{detail}; expected={Convert.ToHexString(expected)} actual={Convert.ToHexString(actual)}");

    private static uint[] Registers(CpuSandbox cpu) => Enumerable.Range(0, 8).Select(i => cpu[(Reg)i]).ToArray();

    private static string Context(byte[] code, string detail, uint[] registers) =>
        $"bytes={Convert.ToHexString(code)} {detail}; inputs=" +
        string.Join(",", registers.Select((value, i) => $"{(Reg)i}:{value:X8}"));

    private static void CheckRegisters(CpuSandbox cpu, uint[] expected, string context)
    {
        for (int i = 0; i < 8; i++)
            Assert.True(cpu[(Reg)i] == expected[i],
                $"{context}; {(Reg)i} expected={expected[i]:X8} actual={cpu[(Reg)i]:X8}");
    }
}
