using System;
using System.Collections.Generic;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

public enum FlagProducer { Add, Adc, Sub, Sbb, Cmp, Test, Inc, Dec, Neg }

public sealed class FlagConsumerInstructionTests
{
    public static IEnumerable<object[]> ConditionalShards()
    {
        foreach (FlagProducer producer in Enum.GetValues<FlagProducer>())
        foreach (int width in new[] { 8, 16, 32 })
        for (int condition = 0; condition < 16; ++condition)
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { producer, width, condition, mode };
    }

    [Theory]
    [MemberData(nameof(ConditionalShards))]
    public void Setcc_cmovcc_and_jcc_consume_the_same_flags_without_clobbering_them(
        FlagProducer producer, int width, int condition, ExecutionMode mode)
    {
        uint mask = IntegerOracle.Mask(width);
        uint sign = IntegerOracle.Sign(width);
        int vector = 0;
        foreach ((uint left, uint right) in new[] { (0u, 0u), (mask, 1u), (sign - 1, 1u), (sign, 1u), (1u, mask), (sign, mask) })
        {
            uint flags = IntegerOracle.InitialFlags((vector++ & 1) != 0);
            string context = $"{producer}/{width}/condition={condition:X1}/{mode} left=0x{left:X8} right=0x{right:X8} initialFlags=0x{flags:X8}";
            using var cpu = new CpuSandbox();
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            OperandLane lane = width == 8 ? OperandLane.Al : width == 16 ? OperandLane.Ax : OperandLane.Eax;
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, left);
            expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane, right);
            cpu[Reg.EBX] = expected[(int)Reg.EBX];

            IntegerResult result;
            byte[] producerCode;
            if (producer is FlagProducer.Inc or FlagProducer.Dec or FlagProducer.Neg)
            {
                UnaryOperation unary = producer switch
                {
                    FlagProducer.Inc => UnaryOperation.Inc,
                    FlagProducer.Dec => UnaryOperation.Dec,
                    _ => UnaryOperation.Neg
                };
                result = IntegerOracle.Unary(unary, width, left, flags);
                producerCode = IntegerTestSupport.UnaryCode(unary, lane);
                expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);
            }
            else
            {
                BinaryOperation binary = producer switch
                {
                    FlagProducer.Add => BinaryOperation.Add, FlagProducer.Adc => BinaryOperation.Adc,
                    FlagProducer.Sub => BinaryOperation.Sub, FlagProducer.Sbb => BinaryOperation.Sbb,
                    FlagProducer.Cmp => BinaryOperation.Cmp, _ => BinaryOperation.Test
                };
                result = IntegerOracle.Binary(binary, width, left, right, flags);
                producerCode = IntegerTestSupport.BinaryCode(binary, lane, BinaryEncoding.Register, 0);
                expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane,
                    binary is BinaryOperation.Cmp or BinaryOperation.Test ? left : result.Value);
            }

            bool take = IntegerOracle.Condition(condition, result.Flags);
            expected[(int)Reg.ECX] = (expected[(int)Reg.ECX] & 0xffffff00) | (take ? 1u : 0u);
            if (take) expected[(int)Reg.EDX] = expected[(int)Reg.ESI];
            expected[(int)Reg.EDI] = take ? 0x13579bdfu : 0x2468ace0u;

            var code = new List<byte>(producerCode);
            code.AddRange(new byte[] { 0x0f, (byte)(0x90 + condition), 0xc1 }); // SETcc CL.
            code.AddRange(new byte[] { 0x0f, (byte)(0x40 + condition), 0xd6 }); // CMOVcc EDX, ESI.
            code.AddRange(new byte[] { (byte)(0x70 + condition), 7 });
            code.Add(0xbf); // MOV EDI, false marker.
            IntegerTestSupport.AppendValue(code, 0x2468ace0, 32);
            code.AddRange(new byte[] { 0xeb, 5 });
            code.Add(0xbf); // MOV EDI, true marker.
            IntegerTestSupport.AppendValue(code, 0x13579bdf, 32);

            // Step observes flags between instructions; Run keeps the producer
            // and all three consumers inside a normal cached instruction block.
            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }

    public static IEnumerable<object[]> CarryChainShards()
    {
        foreach (int width in new[] { 8, 16, 32 })
        foreach (bool subtract in new[] { false, true })
        foreach (bool interveningIncrement in new[] { false, true })
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { width, subtract, interveningIncrement, mode };
    }

    [Theory]
    [MemberData(nameof(CarryChainShards))]
    public void Adc_sbb_observe_carry_even_across_inc_which_preserves_it(
        int width, bool subtract, bool interveningIncrement, ExecutionMode mode)
    {
        uint mask = IntegerOracle.Mask(width);
        uint sign = IntegerOracle.Sign(width);
        foreach ((uint left, uint right) in new[] { (0u, 0u), (mask, 1u), (sign - 1, 1u), (sign, mask) })
        foreach (uint secondLeft in new[] { 0u, sign, mask })
        {
            string context = $"carry-chain/{(subtract ? "SUB/SBB" : "ADD/ADC")}/{width}/inc={interveningIncrement}/{mode} left=0x{left:X8} right=0x{right:X8} secondLeft=0x{secondLeft:X8} secondRight=0";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(true);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            OperandLane lane = width == 8 ? OperandLane.Al : width == 16 ? OperandLane.Ax : OperandLane.Eax;
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, left);
            expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane, right);
            cpu[Reg.EBX] = expected[(int)Reg.EBX];
            cpu[Reg.EDX] = IntegerOracle.PutLane(expected[(int)Reg.EDX], lane, secondLeft);
            expected[(int)Reg.ECX] = IntegerOracle.PutLane(expected[(int)Reg.ECX], lane, 0);
            cpu[Reg.ECX] = expected[(int)Reg.ECX];

            BinaryOperation firstOperation = subtract ? BinaryOperation.Sub : BinaryOperation.Add;
            BinaryOperation secondOperation = subtract ? BinaryOperation.Sbb : BinaryOperation.Adc;
            IntegerResult first = IntegerOracle.Binary(firstOperation, width, left, right, flags);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, first.Value);
            var code = new List<byte>(IntegerTestSupport.BinaryCode(firstOperation, lane, BinaryEncoding.Register, 0));
            if (interveningIncrement)
            {
                IntegerResult increment = IntegerOracle.Unary(UnaryOperation.Inc, 32, expected[(int)Reg.EDI], first.Flags);
                expected[(int)Reg.EDI] = increment.Value;
                first = first with { Flags = increment.Flags };
                code.Add(0x47); // INC EDI must not overwrite the earlier carry.
            }
            IntegerResult second = IntegerOracle.Binary(secondOperation, width, secondLeft, 0, first.Flags);
            expected[(int)Reg.EDX] = IntegerOracle.PutLane(expected[(int)Reg.EDX], lane, second.Value);
            code.AddRange(IntegerTestSupport.Prefix(width));
            code.Add((byte)(IntegerTestSupport.Group(secondOperation) * 8 + (width == 8 ? 0 : 1)));
            code.Add(0xca); // ADC/SBB DL/DX/EDX, CL/CX/ECX.

            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, second.Flags, second.FlagMask, context);
        }
    }
}
