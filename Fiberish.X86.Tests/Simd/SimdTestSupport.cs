using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

internal static class SimdTestSupport
{
    public static CpuSandbox Create()
    {
        var cpu = new CpuSandbox();
        for (int i = 0; i < 8; i++)
        {
            cpu[(Reg)i] = 0x12340000u + (uint)i * 0x101u;
            cpu.WriteXmm(i, Enumerable.Range(0, 16).Select(j => (byte)(i * 19 + j * 7)).ToArray());
        }
        cpu[Reg.ESP] = CpuSandbox.StackAddress + 0x800;
        cpu.Eflags = 0xAD7; // All arithmetic flags set, plus IF and the fixed bit.
        return cpu;
    }

    public static byte[] Lanes(int width, params ulong[] values)
    {
        var result = new byte[16];
        for (int i = 0; i < values.Length; i++)
            WriteLane(result, i, width, values[i]);
        return result;
    }

    public static ulong ReadLane(byte[] bytes, int lane, int width) => width switch
    {
        8 => bytes[lane],
        16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lane * 2)),
        32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * 4)),
        64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(lane * 8)),
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };

    public static void WriteLane(byte[] bytes, int lane, int width, ulong value)
    {
        switch (width)
        {
            case 8: bytes[lane] = (byte)value; break;
            case 16: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(lane * 2), (ushort)value); break;
            case 32: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(lane * 4), (uint)value); break;
            case 64: BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(lane * 8), value); break;
            default: throw new ArgumentOutOfRangeException(nameof(width));
        }
    }

    public static long Signed(ulong value, int width) => width == 64
        ? unchecked((long)value)
        : unchecked((long)(value << (64 - width))) >> (64 - width);

    public static byte[] Instruction(byte prefix, byte opcode, bool memory = false, byte? immediate = null)
    {
        var bytes = new List<byte>();
        if (prefix != 0) bytes.Add(prefix);
        bytes.AddRange(new byte[] { 0x0F, opcode, memory ? (byte)0x00 : (byte)0xC1 });
        if (immediate.HasValue) bytes.Add(immediate.Value);
        return bytes.ToArray();
    }

    // Every successful SIMD instruction must leave unrelated architectural state alone.
    public static void Execute(CpuSandbox cpu, byte[] code, byte[]? expectedXmm = null,
        int xmmDestination = 0, Reg? registerDestination = null, uint registerValue = 0, string detail = "")
    {
        uint[] registers = Enumerable.Range(0, 8).Select(i => cpu[(Reg)i]).ToArray();
        byte[][] xmm = Enumerable.Range(0, 8).Select(cpu.ReadXmm).ToArray();
        uint flags = cpu.Eflags;
        string context = $"bytes={Convert.ToHexString(code)} {detail}; " +
            $"xmm0={Convert.ToHexString(xmm[0])} xmm1={Convert.ToHexString(xmm[1])}";
        cpu.WriteCode(code);
        Assert.True(cpu.Step() == EmuStatus.Running,
            $"{context}; status={cpu.Status} vector={cpu.FaultVector} address={cpu.FaultAddress:X8}");
        Assert.True(cpu.Eip == CpuSandbox.CodeAddress + code.Length, $"{context}; EIP={cpu.Eip:X8}");
        Assert.True(cpu.Eflags == flags, $"{context}; EFLAGS expected={flags:X8} actual={cpu.Eflags:X8}");
        for (int i = 0; i < 8; i++)
        {
            uint expected = registerDestination == (Reg)i ? registerValue : registers[i];
            Assert.True(cpu[(Reg)i] == expected,
                $"{context}; {(Reg)i} expected={expected:X8} actual={cpu[(Reg)i]:X8}");
            Bytes(expectedXmm != null && i == xmmDestination ? expectedXmm : xmm[i], cpu.ReadXmm(i),
                $"{context}; xmm{i}");
        }
    }

    public static void Bytes(byte[] expected, byte[] actual, string context) =>
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{context}; expected={Convert.ToHexString(expected)} actual={Convert.ToHexString(actual)}");

    public static void ConversionFault(CpuSandbox cpu, byte[] code, uint expectedMxcsr, string detail)
    {
        uint[] registers = Enumerable.Range(0, 8).Select(i => cpu[(Reg)i]).ToArray();
        byte[][] xmm = Enumerable.Range(0, 8).Select(cpu.ReadXmm).ToArray();
        uint flags = cpu.Eflags;
        string context = $"bytes={Convert.ToHexString(code)} {detail}; xmm1={Convert.ToHexString(xmm[1])}";
        cpu.WriteCode(code);
        Assert.True(cpu.Step() == EmuStatus.Fault, $"{context}; expected #XM, actual status={cpu.Status}");
        Assert.True(cpu.FaultVector == 19, $"{context}; expected #XM(19), actual={cpu.FaultVector}");
        Assert.True(cpu.Eip == CpuSandbox.CodeAddress, $"{context}; fault EIP={cpu.Eip:X8}");
        Assert.True(cpu.Eflags == flags, $"{context}; EFLAGS {flags:X8}->{cpu.Eflags:X8}");
        Assert.True(cpu.Mxcsr == expectedMxcsr,
            $"{context}; MXCSR expected={expectedMxcsr:X8} actual={cpu.Mxcsr:X8}");
        for (int i = 0; i < 8; i++)
        {
            Assert.True(cpu[(Reg)i] == registers[i], $"{context}; fault changed {(Reg)i}");
            Bytes(xmm[i], cpu.ReadXmm(i), $"{context}; fault changed xmm{i}");
        }
    }
}
