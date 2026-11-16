using System;
using System.Collections.Generic;
using System.Linq;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

public sealed class ShiftRotateInstructionTests
{
    public static IEnumerable<object[]> Shards()
    {
        foreach (ShiftOperation operation in Enum.GetValues<ShiftOperation>())
        foreach (OperandLane lane in Enum.GetValues<OperandLane>())
        foreach (ShiftEncoding encoding in Enum.GetValues<ShiftEncoding>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { operation, lane, encoding, mode };
    }

    [Theory]
    [MemberData(nameof(Shards))]
    public void Counts_are_masked_before_shifting_and_only_defined_flags_are_checked(
        ShiftOperation operation, OperandLane lane, ShiftEncoding encoding, ExecutionMode mode)
    {
        int width = IntegerOracle.Width(lane);
        IEnumerable<int> counts = encoding == ShiftEncoding.One ? new[] { 1 }
            : new[] { 0, 1, width - 1, width, width + 1, 31, 32, 33, 255 }.Distinct();
        foreach (uint input in new[] { 0u, 1u, IntegerOracle.Sign(width), IntegerOracle.Mask(width) })
        foreach (int count in counts)
        foreach (bool setFlags in new[] { false, true })
        {
            string context = $"{operation}/{width}/{lane}/{encoding}/{mode} input=0x{input:X8} count={count} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, input);
            if (encoding == ShiftEncoding.Cl)
            {
                expected[(int)Reg.ECX] = (expected[(int)Reg.ECX] & 0xffffff00) | (byte)count;
                cpu[Reg.ECX] = expected[(int)Reg.ECX];
            }
            IntegerResult result = IntegerOracle.Shift(operation, width, input, (byte)count, flags);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, result.Value);
            IntegerTestSupport.Execute(cpu, IntegerTestSupport.ShiftCode(operation, lane, encoding, (byte)count), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }
}
