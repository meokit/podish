using System.Collections.Generic;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

[Trait("Category", "Exhaustive")]
public sealed class ExhaustiveByteAluTests
{
    public static IEnumerable<object[]> Shards()
    {
        foreach (BinaryOperation operation in new[] { BinaryOperation.Add, BinaryOperation.Sub })
        for (int highNibble = 0; highNibble < 16; ++highNibble)
            yield return new object[] { operation, highNibble };
    }

    // 32 independently reported shards, 131,072 total fresh sandboxes. Keeping
    // exhaustive coverage to ADD/SUB avoids multiplying this cost by every ALU
    // family and execution mode; the fast suite covers carry inputs and Run.
    [Theory]
    [MemberData(nameof(Shards))]
    public void All_byte_operand_pairs_match_mathematical_flags(BinaryOperation operation, int highNibble)
    {
        byte[] code = IntegerTestSupport.BinaryCode(operation, OperandLane.Al, BinaryEncoding.Register, 0);
        for (uint left = (uint)highNibble << 4; left < ((uint)(highNibble + 1) << 4); ++left)
        for (uint right = 0; right < 256; ++right)
        {
            string context = $"exhaustive/{operation}/8/Al/Register/Step left=0x{left:X2} right=0x{right:X2}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(((left ^ right) & 1) != 0);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], OperandLane.Al, left);
            expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], OperandLane.Al, right);
            cpu[Reg.EBX] = expected[(int)Reg.EBX];
            IntegerResult result = IntegerOracle.Binary(operation, 8, left, right, flags);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], OperandLane.Al, result.Value);
            IntegerTestSupport.Execute(cpu, code, ExecutionMode.Step, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
        }
    }
}
