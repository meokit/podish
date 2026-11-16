using System;
using System.Collections.Generic;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

internal static class IntegerTestSupport
{
    internal static uint[] Initialize(CpuSandbox cpu, uint flags)
    {
        // No shared mutable vectors or CPU instances; each instruction gets a
        // newly-created sandbox and distinct non-destination register sentinels.
        uint[] registers =
        {
            0xa5c37e91, 0x96d24b68, 0x5a8f3127, 0xd46b28f0,
            CpuSandbox.StackAddress + 4096 - 16, 0x3478c2a9, 0x6e91b5d3, 0x73ac592e
        };
        for (int index = 0; index < registers.Length; ++index)
            cpu[(Reg)index] = registers[index];
        cpu.Eflags = flags;
        return registers;
    }

    internal static void Execute(CpuSandbox cpu, byte[] code, ExecutionMode mode, string context)
    {
        cpu.WriteCode(code);
        ExecuteToEnd(cpu, code.Length, mode, context);
    }

    internal static void ExecuteToEnd(CpuSandbox cpu, int length, ExecutionMode mode, string context)
    {
        uint end = CpuSandbox.CodeAddress + (uint)length;
        if (mode == ExecutionMode.Run)
        {
            cpu.Run(end, 100);
        }
        else
        {
            int instructions = 0;
            while (cpu.Eip != end && instructions++ < 100)
            {
                Assert.True(cpu.Eip >= CpuSandbox.CodeAddress && cpu.Eip < end,
                    $"{context}: unexpected intermediate EIP=0x{cpu.Eip:X8}");
                EmuStatus status = cpu.Step();
                Assert.True(status != EmuStatus.Fault,
                    $"{context}: Step fault vector={cpu.FaultVector}, EIP=0x{cpu.Eip:X8}");
            }
        }
        Assert.True(cpu.Status != EmuStatus.Fault,
            $"{context}: fault vector={cpu.FaultVector}, EIP=0x{cpu.Eip:X8}");
        Equal(end, cpu.Eip, context, "EIP");
    }

    internal static void AssertRegisters(CpuSandbox cpu, uint[] expected, string context)
    {
        for (int index = 0; index < expected.Length; ++index)
            Equal(expected[index], cpu[(Reg)index], context, ((Reg)index).ToString());
    }

    internal static void AssertFlags(CpuSandbox cpu, uint expected, uint validityMask, string context)
    {
        uint actual = cpu.Eflags;
        Assert.True((actual & validityMask) == (expected & validityMask),
            $"{context}: EFLAGS expected=0x{expected:X8}, actual=0x{actual:X8}, defined/preserved mask=0x{validityMask:X8}");
    }

    internal static void Equal(uint expected, uint actual, string context, string name) =>
        Assert.True(expected == actual, $"{context}: {name} expected=0x{expected:X8}, actual=0x{actual:X8}");

    internal static int Group(BinaryOperation operation) => operation switch
    {
        BinaryOperation.Add => 0, BinaryOperation.Or => 1, BinaryOperation.Adc => 2,
        BinaryOperation.Sbb => 3, BinaryOperation.And => 4, BinaryOperation.Sub => 5,
        BinaryOperation.Xor => 6, BinaryOperation.Cmp => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal static int RegisterCode(OperandLane lane, bool source = false) =>
        (source ? 3 : 0) + (lane == OperandLane.Ah ? 4 : 0);

    internal static List<byte> Prefix(int width) => width == 16 ? new List<byte> { 0x66 } : new List<byte>();

    internal static void AppendValue(List<byte> code, uint value, int width)
    {
        for (int i = 0; i < width / 8; ++i)
            code.Add((byte)(value >> (i * 8)));
    }

    internal static byte[] Bytes(uint value, int width)
    {
        var bytes = new List<byte>();
        AppendValue(bytes, value, width);
        return bytes.ToArray();
    }

    internal static byte[] BinaryCode(BinaryOperation operation, OperandLane lane, BinaryEncoding encoding, uint immediate)
    {
        int width = IntegerOracle.Width(lane);
        var code = Prefix(width);
        int destination = RegisterCode(lane);
        if (encoding == BinaryEncoding.Register)
        {
            code.Add(operation == BinaryOperation.Test ? (byte)(width == 8 ? 0x84 : 0x85)
                : (byte)(Group(operation) * 8 + (width == 8 ? 0 : 1)));
            code.Add((byte)(0xc0 | (RegisterCode(lane, source: true) << 3) | destination));
        }
        else if (lane != OperandLane.Ah)
        {
            // Accumulator-immediate opcodes are a different decode path from
            // the ModRM and sign-extended group-immediate forms below.
            code.Add(operation == BinaryOperation.Test ? (byte)(width == 8 ? 0xa8 : 0xa9)
                : (byte)(Group(operation) * 8 + (width == 8 ? 4 : 5)));
            AppendValue(code, immediate, width);
        }
        else
        {
            code.Add(operation == BinaryOperation.Test ? (byte)0xf6 : (byte)0x80);
            code.Add((byte)(0xc0 | (operation == BinaryOperation.Test ? 0 : Group(operation) << 3) | destination));
            AppendValue(code, immediate, width);
        }
        return code.ToArray();
    }

    internal static byte[] UnaryCode(UnaryOperation operation, OperandLane lane, bool compact = false)
    {
        int width = IntegerOracle.Width(lane);
        var code = Prefix(width);
        if (compact)
        {
            if (width == 8 || operation is not (UnaryOperation.Inc or UnaryOperation.Dec))
                throw new ArgumentException("The compact form is only INC/DEC r16/r32.");
            code.Add(operation == UnaryOperation.Inc ? (byte)0x40 : (byte)0x48);
        }
        else
        {
            bool incrementFamily = operation is UnaryOperation.Inc or UnaryOperation.Dec;
            code.Add(incrementFamily ? (byte)(width == 8 ? 0xfe : 0xff) : (byte)(width == 8 ? 0xf6 : 0xf7));
            int group = operation switch { UnaryOperation.Inc => 0, UnaryOperation.Dec => 1, UnaryOperation.Not => 2, _ => 3 };
            code.Add((byte)(0xc0 | (group << 3) | RegisterCode(lane)));
        }
        return code.ToArray();
    }

    internal static byte[] ShiftCode(ShiftOperation operation, OperandLane lane, ShiftEncoding encoding, byte count)
    {
        int width = IntegerOracle.Width(lane);
        var code = Prefix(width);
        int group = operation switch
        {
            ShiftOperation.Rol => 0, ShiftOperation.Ror => 1, ShiftOperation.Rcl => 2,
            ShiftOperation.Rcr => 3, ShiftOperation.Shl => 4, ShiftOperation.Shr => 5, _ => 7
        };
        int opcode = encoding switch { ShiftEncoding.Immediate => 0xc0, ShiftEncoding.Cl => 0xd2, _ => 0xd0 };
        code.Add((byte)(opcode + (width == 8 ? 0 : 1)));
        code.Add((byte)(0xc0 | (group << 3) | RegisterCode(lane)));
        if (encoding == ShiftEncoding.Immediate) code.Add(count);
        return code.ToArray();
    }
}
