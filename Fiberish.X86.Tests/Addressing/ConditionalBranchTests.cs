using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class ConditionalBranchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Jcc_ShortAndNearEvaluateEveryConditionAndPreserveFlags(int condition)
    {
        for (uint combination = 0; combination < 32; combination++)
        foreach (bool near in new[] { false, true })
        foreach (int displacement in new[] { -16, 16 })
        {
            using var cpu = AddressingTestSupport.Create();
            bool cf = (combination & 1) != 0, pf = (combination & 2) != 0;
            bool zf = (combination & 4) != 0, sf = (combination & 8) != 0, of = (combination & 16) != 0;
            uint flags = 0x212u | (cf ? 1u : 0) | (pf ? 4u : 0) | (zf ? 0x40u : 0)
                | (sf ? 0x80u : 0) | (of ? 0x800u : 0);
            cpu.Eflags = flags;
            bool taken = Condition(condition, cf, pf, zf, sf, of);
            byte[] code = near
                ? AddressingTestSupport.WithDisplacement(new byte[] { 0x0F, (byte)(0x80 | condition) }, unchecked((uint)displacement))
                : new byte[] { (byte)(0x70 | condition), unchecked((byte)displacement) };
            cpu.WriteCode(code);
            uint next = unchecked(CpuSandbox.CodeAddress + (uint)code.Length + (taken ? (uint)displacement : 0));
            AddressingTestSupport.Step(cpu, code,
                $"Jcc cc={condition:X1} near={near} displacement={displacement} flags={flags:X8} taken={taken}", next);
        }
    }

    [Theory]
    [InlineData(false, 0u, true)]
    [InlineData(false, 1u, false)]
    [InlineData(false, 0x10000u, false)]
    [InlineData(true, 0u, true)]
    [InlineData(true, 1u, false)]
    [InlineData(true, 0x10000u, true)]
    [InlineData(true, 0xFFFF0000u, true)]
    public void JecxzAndAddressOverrideJcxz_UseCorrectCounterWidth(bool address16, uint ecx, bool taken)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.ECX] = ecx;
        byte[] code = address16 ? new byte[] { 0x67, 0xE3, 0x10 } : new byte[] { 0xE3, 0x10 };
        cpu.WriteCode(code);
        AddressingTestSupport.Step(cpu, code, $"JCXZ addressWidth={(address16 ? 16 : 32)} ECX={ecx:X8} taken={taken}",
            CpuSandbox.CodeAddress + (uint)code.Length + (taken ? 16u : 0));
    }

    private static bool Condition(int condition, bool cf, bool pf, bool zf, bool sf, bool of) => condition switch
    {
        0 => of, 1 => !of, 2 => cf, 3 => !cf, 4 => zf, 5 => !zf, 6 => cf || zf, 7 => !cf && !zf,
        8 => sf, 9 => !sf, 10 => pf, 11 => !pf, 12 => sf != of, 13 => sf == of,
        14 => zf || sf != of, 15 => !zf && sf == of,
        _ => throw new ArgumentOutOfRangeException(nameof(condition))
    };
}
