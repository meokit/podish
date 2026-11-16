using System;
using System.Collections.Generic;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

public sealed class AluInstructionTests
{
    public static IEnumerable<object[]> BinaryShards()
    {
        foreach (BinaryOperation operation in Enum.GetValues<BinaryOperation>())
        foreach (OperandLane lane in Enum.GetValues<OperandLane>())
        foreach (BinaryEncoding encoding in Enum.GetValues<BinaryEncoding>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { operation, lane, encoding, mode };
    }

    [Theory]
    [MemberData(nameof(BinaryShards))]
    public void Binary_edges_match_independent_bitvector_oracle(
        BinaryOperation operation, OperandLane lane, BinaryEncoding encoding, ExecutionMode mode)
    {
        int width = IntegerOracle.Width(lane);
        foreach (uint left in IntegerOracle.Edges(width))
        foreach (uint right in IntegerOracle.Edges(width))
        foreach (bool setFlags in new[] { false, true })
        {
            string context = $"{operation}/{width}/{lane}/{encoding}/{mode} left=0x{left:X8} right=0x{right:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint initialFlags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, initialFlags);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, left);
            cpu[Reg.EAX] = expected[(int)Reg.EAX];
            if (encoding == BinaryEncoding.Register)
            {
                expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane, right);
                cpu[Reg.EBX] = expected[(int)Reg.EBX];
            }
            IntegerResult result = IntegerOracle.Binary(operation, width, left, right, initialFlags);
            if (operation is not (BinaryOperation.Cmp or BinaryOperation.Test))
                expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);

            IntegerTestSupport.Execute(cpu, IntegerTestSupport.BinaryCode(operation, lane, encoding, right), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }

    public static IEnumerable<object[]> MemoryShards()
    {
        foreach (BinaryOperation operation in Enum.GetValues<BinaryOperation>())
        foreach (int width in new[] { 8, 16, 32 })
        foreach (bool memoryDestination in new[] { false, true })
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { operation, width, memoryDestination, mode };
    }

    [Theory]
    [MemberData(nameof(MemoryShards))]
    public void Memory_ALU_preserves_guard_bytes_and_non_destinations(
        BinaryOperation operation, int width, bool memoryDestination, ExecutionMode mode)
    {
        uint mask = IntegerOracle.Mask(width);
        uint sign = IntegerOracle.Sign(width);
        foreach ((uint left, uint right) in new[] { (0u, 0u), (mask, 1u), (sign - 1, 1u), (sign, mask), (0x0fu, 1u) })
        foreach (bool setFlags in new[] { false, true })
        {
            string context = $"{operation}/{width}/memoryDestination={memoryDestination}/{mode} left=0x{left:X8} right=0x{right:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint initialFlags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, initialFlags);
            OperandLane lane = width == 8 ? OperandLane.Al : width == 16 ? OperandLane.Ax : OperandLane.Eax;
            uint registerValue = memoryDestination ? right : left;
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, registerValue);
            cpu[Reg.EAX] = expected[(int)Reg.EAX];

            uint bufferAddress = CpuSandbox.DataAddress + 32;
            uint operandAddress = bufferAddress + 5; // Deliberately unaligned.
            byte[] expectedMemory = { 0x31, 0x72, 0xa9, 0xc4, 0x5b, 0, 0, 0, 0, 0xe3, 0x26, 0x48, 0x9d, 0xf0, 0x15, 0x67 };
            byte[] input = IntegerTestSupport.Bytes(memoryDestination ? left : right, width);
            input.CopyTo(expectedMemory, 5);
            cpu.WriteMemory(bufferAddress, expectedMemory);

            var code = IntegerTestSupport.Prefix(width);
            int opcode = operation == BinaryOperation.Test ? (width == 8 ? 0x84 : 0x85)
                : IntegerTestSupport.Group(operation) * 8 + (width == 8 ? 0 : 1) + (memoryDestination ? 0 : 2);
            code.Add((byte)opcode);
            code.Add(0x05); // [disp32], accumulator register.
            IntegerTestSupport.AppendValue(code, operandAddress, 32);

            IntegerResult result = IntegerOracle.Binary(operation, width, left, right, initialFlags);
            if (operation is not (BinaryOperation.Cmp or BinaryOperation.Test))
            {
                if (memoryDestination)
                    IntegerTestSupport.Bytes(result.Value, width).CopyTo(expectedMemory, 5);
                else
                    expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);
            }
            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
            byte[] actualMemory = cpu.ReadMemory(bufferAddress, expectedMemory.Length);
            for (int i = 0; i < expectedMemory.Length; ++i)
                IntegerTestSupport.Equal(expectedMemory[i], actualMemory[i], context, $"memory[+{i}]");
        }
    }

    public static IEnumerable<object[]> SignExtendedShards()
    {
        foreach (BinaryOperation operation in Enum.GetValues<BinaryOperation>())
        {
            if (operation == BinaryOperation.Test) continue; // TEST has no opcode 83 form.
            foreach (int width in new[] { 16, 32 })
            foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
                yield return new object[] { operation, width, mode };
        }
    }

    [Theory]
    [MemberData(nameof(SignExtendedShards))]
    public void Group_83_sign_extends_its_byte_immediate(BinaryOperation operation, int width, ExecutionMode mode)
    {
        foreach (uint left in new[] { 0u, IntegerOracle.Sign(width), IntegerOracle.Mask(width) })
        foreach (byte encodedImmediate in new byte[] { 0, 1, 0x7f, 0x80, 0xff })
        foreach (bool setFlags in new[] { false, true })
        {
            uint right = unchecked((uint)(int)(sbyte)encodedImmediate) & IntegerOracle.Mask(width);
            string context = $"{operation}/{width}/group83/{mode} left=0x{left:X8} imm8=0x{encodedImmediate:X2} right=0x{right:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            OperandLane lane = width == 16 ? OperandLane.Ax : OperandLane.Eax;
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, left);
            cpu[Reg.EAX] = expected[(int)Reg.EAX];
            IntegerResult result = IntegerOracle.Binary(operation, width, left, right, flags);
            if (operation != BinaryOperation.Cmp)
                expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);
            var code = IntegerTestSupport.Prefix(width);
            code.Add(0x83);
            code.Add((byte)(0xc0 | (IntegerTestSupport.Group(operation) << 3)));
            code.Add(encodedImmediate);
            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }

    public static IEnumerable<object[]> UnaryShards()
    {
        foreach (UnaryOperation operation in Enum.GetValues<UnaryOperation>())
        foreach (OperandLane lane in Enum.GetValues<OperandLane>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
        {
            yield return new object[] { operation, lane, false, mode };
            if ((lane is OperandLane.Ax or OperandLane.Eax) && (operation is UnaryOperation.Inc or UnaryOperation.Dec))
                yield return new object[] { operation, lane, true, mode };
        }
    }

    [Theory]
    [MemberData(nameof(UnaryShards))]
    public void Unary_edges_preserve_partial_registers_and_required_flags(
        UnaryOperation operation, OperandLane lane, bool compact, ExecutionMode mode)
    {
        int width = IntegerOracle.Width(lane);
        foreach (uint input in IntegerOracle.Edges(width))
        foreach (bool setFlags in new[] { false, true })
        {
            string context = $"{operation}/{width}/{lane}/compact={compact}/{mode} input=0x{input:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, input);
            IntegerResult result = IntegerOracle.Unary(operation, width, input, flags);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);
            IntegerTestSupport.Execute(cpu, IntegerTestSupport.UnaryCode(operation, lane, compact), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }
}
