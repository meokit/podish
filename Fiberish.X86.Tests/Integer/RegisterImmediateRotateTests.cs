using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

public sealed class RegisterImmediateRotateTests
{
    [Theory]
    [InlineData(16, false, false)]
    [InlineData(16, false, true)]
    [InlineData(16, true, false)]
    [InlineData(16, true, true)]
    [InlineData(32, false, false)]
    [InlineData(32, false, true)]
    [InlineData(32, true, false)]
    [InlineData(32, true, true)]
    public void AllRegistersCountsFlagsAndDeadFlagForms(int width, bool right, bool flagsDead)
    {
        for (int register = 0; register < 8; register++)
        {
            using var cpu = new CpuSandbox();
            foreach (int count in new[] { 0, 1, 2, 7, 8, 15, 16, 17, 31, 32, 33, 255 })
            foreach (uint input in new[] { 0u, 1u, IntegerOracle.Sign(width), IntegerOracle.Mask(width), 0x95A53C69u })
            foreach (bool set in new[] { false, true })
            {
                uint flags = IntegerOracle.InitialFlags(set);
                uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
                var lane = width == 16 ? OperandLane.Ax : OperandLane.Eax;
                expected[register] = IntegerOracle.PutLane(expected[register], lane, input);
                cpu[(Reg)register] = expected[register];
                var result = IntegerOracle.Shift(right ? ShiftOperation.Ror : ShiftOperation.Rol, width, input, (byte)count, flags);
                expected[register] = IntegerOracle.PutLane(expected[register], lane, result.Value);
                var code = new List<byte>();
                if (width == 16) code.Add(0x66);
                code.AddRange(new byte[] { 0xC1, (byte)(0xC0 | (right ? 8 : 0) | register), (byte)count });
                if (flagsDead)
                {
                    int other = (register + 1) % 8;
                    code.AddRange(new byte[] { 0x31, (byte)(0xC0 | (other << 3) | other) });
                    expected[other] = 0;
                }
                string context = $"width={width} right={right} reg={register} count={count} input={input:X8} initial={flags:X8} dead={flagsDead}";
                IntegerTestSupport.Execute(cpu, code.ToArray(), ExecutionMode.Run, context);
                IntegerTestSupport.AssertRegisters(cpu, expected, context);
                if (!flagsDead) IntegerTestSupport.AssertFlags(cpu, result.Flags, result.FlagMask, context);
            }
        }
    }
}
