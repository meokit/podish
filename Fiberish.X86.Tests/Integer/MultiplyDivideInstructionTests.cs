using System;
using System.Collections.Generic;
using System.Numerics;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Integer;

public enum ImulForm { TwoOperand, FullImmediate, ByteImmediate }
public enum DivideFaultKind { ZeroDivisor, QuotientOverflow, MinimumDividendByMinusOne }

public sealed class MultiplyDivideInstructionTests
{
    public static IEnumerable<object[]> ImplicitMultiplyShards()
    {
        foreach (bool signed in new[] { false, true })
        foreach (OperandLane sourceLane in Enum.GetValues<OperandLane>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { signed, sourceLane, mode };
    }

    [Theory]
    [MemberData(nameof(ImplicitMultiplyShards))]
    public void Implicit_multiply_writes_the_complete_double_width_product(
        bool signed, OperandLane sourceLane, ExecutionMode mode)
    {
        int width = IntegerOracle.Width(sourceLane);
        foreach (uint left in IntegerOracle.Edges(width))
        foreach (uint right in IntegerOracle.Edges(width))
        foreach (bool setFlags in new[] { false, true })
        {
            string context = $"{(signed ? "IMUL" : "MUL")}/{width}/source={sourceLane}/{mode} left=0x{left:X8} right=0x{right:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            OperandLane accumulatorLane = width == 8 ? OperandLane.Al : width == 16 ? OperandLane.Ax : OperandLane.Eax;
            cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], accumulatorLane, left);
            expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], sourceLane, right);
            cpu[Reg.EBX] = expected[(int)Reg.EBX];

            BigInteger product = signed
                ? new BigInteger(IntegerOracle.Signed(left, width)) * IntegerOracle.Signed(right, width)
                : new BigInteger(left) * right;
            BigInteger bits = IntegerOracle.BitPattern(product, width * 2);
            SetDoubleWidthExpected(expected, width, bits);
            bool overflow = signed
                ? product < -(BigInteger.One << (width - 1)) || product >= (BigInteger.One << (width - 1))
                : product > IntegerOracle.Mask(width);
            uint expectedFlags = (flags & ~(IntegerOracle.Cf | IntegerOracle.Of))
                | (overflow ? IntegerOracle.Cf | IntegerOracle.Of : 0);
            uint validity = ~(IntegerOracle.Pf | IntegerOracle.Af | IntegerOracle.Zf | IntegerOracle.Sf);
            var code = IntegerTestSupport.Prefix(width);
            code.Add(width == 8 ? (byte)0xf6 : (byte)0xf7);
            code.Add((byte)(0xc0 | ((signed ? 5 : 4) << 3) | IntegerTestSupport.RegisterCode(sourceLane, source: true)));

            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, expectedFlags, validity, context);
        }
    }

    public static IEnumerable<object[]> TruncatedMultiplyShards()
    {
        foreach (int width in new[] { 16, 32 })
        foreach (ImulForm form in Enum.GetValues<ImulForm>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { width, form, mode };
    }

    [Theory]
    [MemberData(nameof(TruncatedMultiplyShards))]
    public void Truncated_imul_reports_signed_overflow_and_sign_extends_immediates(int width, ImulForm form, ExecutionMode mode)
    {
        IEnumerable<uint> rightValues = form == ImulForm.ByteImmediate
            ? new[] { 0u, 1u, 0x7fu, 0x80u, 0xffu }
            : IntegerOracle.Edges(width);
        foreach (uint left in IntegerOracle.Edges(width))
        foreach (uint encodedRight in rightValues)
        foreach (bool setFlags in new[] { false, true })
        {
            uint right = form == ImulForm.ByteImmediate
                ? unchecked((uint)(int)(sbyte)(byte)encodedRight) & IntegerOracle.Mask(width)
                : encodedRight;
            string context = $"IMUL/{width}/{form}/{mode} left=0x{left:X8} right=0x{right:X8} encoded=0x{encodedRight:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            OperandLane lane = width == 16 ? OperandLane.Ax : OperandLane.Eax;
            if (form == ImulForm.TwoOperand)
            {
                cpu[Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, left);
                expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane, right);
            }
            else
            {
                expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane, left);
            }
            cpu[Reg.EBX] = expected[(int)Reg.EBX];
            BigInteger product = new BigInteger(IntegerOracle.Signed(left, width)) * IntegerOracle.Signed(right, width);
            uint low = (uint)IntegerOracle.BitPattern(product, width);
            expected[(int)Reg.EAX] = IntegerOracle.PutLane(expected[(int)Reg.EAX], lane, low);
            bool overflow = product < -(BigInteger.One << (width - 1)) || product >= (BigInteger.One << (width - 1));
            uint expectedFlags = (flags & ~(IntegerOracle.Cf | IntegerOracle.Of))
                | (overflow ? IntegerOracle.Cf | IntegerOracle.Of : 0);
            uint validity = ~(IntegerOracle.Pf | IntegerOracle.Af | IntegerOracle.Zf | IntegerOracle.Sf);
            var code = IntegerTestSupport.Prefix(width);
            if (form == ImulForm.TwoOperand)
            {
                code.Add(0x0f);
                code.Add(0xaf);
                code.Add(0xc3); // IMUL AX/EAX, BX/EBX.
            }
            else
            {
                code.Add(form == ImulForm.ByteImmediate ? (byte)0x6b : (byte)0x69);
                code.Add(0xc3); // IMUL AX/EAX, BX/EBX, immediate.
                IntegerTestSupport.AppendValue(code, encodedRight, form == ImulForm.ByteImmediate ? 8 : width);
            }
            IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            IntegerTestSupport.AssertFlags(cpu, expectedFlags, validity, context);
        }
    }

    public static IEnumerable<object[]> DivideShards()
    {
        foreach (bool signed in new[] { false, true })
        foreach (OperandLane sourceLane in Enum.GetValues<OperandLane>())
        foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
            yield return new object[] { signed, sourceLane, mode };
    }

    [Theory]
    [MemberData(nameof(DivideShards))]
    public void Divide_matches_quotient_remainder_or_faults_without_committing_state(
        bool signed, OperandLane sourceLane, ExecutionMode mode)
    {
        int width = IntegerOracle.Width(sourceLane);
        foreach ((BigInteger dividend, BigInteger divisor) in DivisionVectors(width, signed))
        foreach (bool setFlags in new[] { false, true })
        {
            BigInteger dividendBits = IntegerOracle.BitPattern(dividend, width * 2);
            uint divisorBits = (uint)IntegerOracle.BitPattern(divisor, width);
            string context = $"{(signed ? "IDIV" : "DIV")}/{width}/source={sourceLane}/{mode} dividend={dividend} bits=0x{dividendBits:X} divisor={divisor} bits=0x{divisorBits:X8} initialSet={setFlags}";
            using var cpu = new CpuSandbox();
            uint flags = IntegerOracle.InitialFlags(setFlags);
            uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
            SetDoubleWidthExpected(expected, width, dividendBits);
            cpu[Reg.EAX] = expected[(int)Reg.EAX];
            cpu[Reg.EDX] = expected[(int)Reg.EDX];
            expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], sourceLane, divisorBits);
            cpu[Reg.EBX] = expected[(int)Reg.EBX];

            BigInteger quotient = divisor.IsZero ? BigInteger.Zero : dividend / divisor;
            BigInteger remainder = divisor.IsZero ? BigInteger.Zero : dividend % divisor;
            BigInteger min = signed ? -(BigInteger.One << (width - 1)) : BigInteger.Zero;
            BigInteger max = signed ? (BigInteger.One << (width - 1)) - 1 : IntegerOracle.Mask(width);
            bool fault = divisor.IsZero || quotient < min || quotient > max;
            var code = IntegerTestSupport.Prefix(width);
            code.Add(width == 8 ? (byte)0xf6 : (byte)0xf7);
            code.Add((byte)(0xc0 | ((signed ? 7 : 6) << 3) | IntegerTestSupport.RegisterCode(sourceLane, source: true)));

            if (fault)
            {
                // A sentinel after the fault must not execute, and the faulting
                // instruction, including a 66 prefix, must retain its start EIP.
                int faultingLength = code.Count;
                code.Add(0x47); // INC EDI (observable if execution continues).
                cpu.WriteCode(code.ToArray());
                if (mode == ExecutionMode.Step)
                    Assert.True(cpu.Step() == EmuStatus.Fault, $"{context}: Step did not report #DE");
                else
                    cpu.Run(CpuSandbox.CodeAddress + (uint)code.Count, 100);
                Assert.True(cpu.Status == EmuStatus.Fault, $"{context}: expected Fault after {faultingLength}-byte divide");
                Assert.True(cpu.FaultVector == 0, $"{context}: expected #DE vector=0, actual={cpu.FaultVector}");
                IntegerTestSupport.Equal(CpuSandbox.CodeAddress, cpu.Eip, context, "fault EIP");
            }
            else
            {
                SetDivisionExpected(expected, width, quotient, remainder);
                IntegerTestSupport.Execute(cpu, code.ToArray(), mode, context);
            }
            IntegerTestSupport.AssertRegisters(cpu, expected, context);
            // DIV/IDIV leave their arithmetic flags undefined. Control flags
            // and all non-result registers are nevertheless preserved.
            IntegerTestSupport.AssertFlags(cpu, flags, ~IntegerOracle.ArithmeticFlags, context);
        }
    }

    public static IEnumerable<object[]> FaultBoundaryShards()
    {
        foreach (int width in new[] { 8, 16, 32 })
        foreach (bool signed in new[] { false, true })
        foreach (DivideFaultKind kind in Enum.GetValues<DivideFaultKind>())
        {
            if (!signed && kind == DivideFaultKind.MinimumDividendByMinusOne) continue;
            foreach (ExecutionMode mode in Enum.GetValues<ExecutionMode>())
                yield return new object[] { width, signed, kind, mode };
        }
    }

    [Theory]
    [MemberData(nameof(FaultBoundaryShards))]
    public void Divide_fault_after_nops_preserves_the_prefixed_instruction_start_and_does_not_execute_sentinels(
        int width, bool signed, DivideFaultKind kind, ExecutionMode mode)
    {
        // These are explicitly known #DE boundaries, not inferred from a host
        // division. In particular, MIN(2*width) / -1 cannot fit a width-bit
        // quotient and must never reach the host's trapping signed division.
        BigInteger dividend = kind switch
        {
            DivideFaultKind.ZeroDivisor => signed ? -BigInteger.One : (BigInteger.One << (width * 2)) - 1,
            DivideFaultKind.MinimumDividendByMinusOne => -(BigInteger.One << (width * 2 - 1)),
            _ => signed ? -(BigInteger.One << (width - 1)) : BigInteger.One << width
        };
        BigInteger divisor = kind == DivideFaultKind.ZeroDivisor ? BigInteger.Zero : signed ? -BigInteger.One : BigInteger.One;
        string context = $"{(signed ? "IDIV" : "DIV")}/{width}/{kind}/{mode} dividend={dividend} divisor={divisor}";
        using var cpu = new CpuSandbox();
        uint flags = IntegerOracle.InitialFlags(true);
        uint[] expected = IntegerTestSupport.Initialize(cpu, flags);
        SetDoubleWidthExpected(expected, width, IntegerOracle.BitPattern(dividend, width * 2));
        cpu[Reg.EAX] = expected[(int)Reg.EAX];
        cpu[Reg.EDX] = expected[(int)Reg.EDX];
        OperandLane lane = width == 8 ? OperandLane.Al : width == 16 ? OperandLane.Ax : OperandLane.Eax;
        expected[(int)Reg.EBX] = IntegerOracle.PutLane(expected[(int)Reg.EBX], lane,
            (uint)IntegerOracle.BitPattern(divisor, width));
        cpu[Reg.EBX] = expected[(int)Reg.EBX];

        var code = new List<byte> { 0x90, 0x90 };
        uint faultEip = CpuSandbox.CodeAddress + (uint)code.Count;
        code.AddRange(IntegerTestSupport.Prefix(width));
        code.Add(width == 8 ? (byte)0xf6 : (byte)0xf7);
        code.Add((byte)(0xc3 | ((signed ? 7 : 6) << 3))); // IDIV/DIV BL/BX/EBX.
        code.Add(0x47); // Register sentinel: INC EDI.
        code.AddRange(new byte[] { 0xc7, 0x05 }); // Memory sentinel: MOV [disp32], imm32.
        uint sentinelAddress = CpuSandbox.DataAddress + 128;
        IntegerTestSupport.AppendValue(code, sentinelAddress, 32);
        IntegerTestSupport.AppendValue(code, 0xdeadbeef, 32);
        cpu.WriteCode(code.ToArray());

        if (mode == ExecutionMode.Step)
        {
            for (int instruction = 0; instruction < 2; ++instruction)
                Assert.True(cpu.Step() != EmuStatus.Fault, $"{context}: leading NOP faulted");
            IntegerTestSupport.Equal(faultEip, cpu.Eip, context, "EIP before divide");
        }
        Exception? exception = Record.Exception(() =>
        {
            if (mode == ExecutionMode.Step) cpu.Step();
            else cpu.Run(CpuSandbox.CodeAddress + (uint)code.Count, 100);
        });
        Assert.True(exception is null, $"{context}: native execution threw instead of reporting guest #DE: {exception}");
        Assert.True(cpu.Status == EmuStatus.Fault, $"{context}: expected guest Fault");
        Assert.True(cpu.FaultVector == 0, $"{context}: expected #DE vector=0, actual={cpu.FaultVector}");
        IntegerTestSupport.Equal(faultEip, cpu.Eip, context, "fault EIP");
        IntegerTestSupport.AssertRegisters(cpu, expected, context);
        IntegerTestSupport.AssertFlags(cpu, flags, ~IntegerOracle.ArithmeticFlags, context);
        byte[] sentinelMemory = cpu.ReadMemory(sentinelAddress, 4);
        for (int index = 0; index < sentinelMemory.Length; ++index)
            IntegerTestSupport.Equal(0, sentinelMemory[index], context, $"memory sentinel[+{index}]");
    }

    private static IEnumerable<(BigInteger Dividend, BigInteger Divisor)> DivisionVectors(int width, bool signed)
    {
        BigInteger mask = IntegerOracle.Mask(width);
        BigInteger sign = BigInteger.One << (width - 1);
        if (!signed)
        {
            yield return (0, 1);
            yield return (mask, 1);
            yield return (mask, mask);
            yield return (mask, 2);
            yield return (BigInteger.One << width, 2);
            yield return (((mask - 1) << width) | mask, mask); // Largest valid quotient.
            yield return ((mask << width) | mask, mask); // Quotient overflow.
            yield return (BigInteger.One << width, 1); // High half == divisor.
            yield return (0, 0);
            yield return ((mask << width) | mask, 0);
            yield return (0x7b, 0x0a); // Nonzero remainder.
        }
        else
        {
            yield return (0, 1);
            yield return (1, 1);
            yield return (-1, 1);
            yield return (-1, -1);
            yield return (-sign, 1);
            yield return (-sign, -1); // Quotient just above the signed maximum.
            yield return (sign - 1, 1);
            yield return (sign - 1, -1);
            yield return (-7, 3);
            yield return (-7, -3);
            yield return (7, -3);
            yield return (7, 3);
            yield return (-(BigInteger.One << (width * 2 - 1)), -1); // Includes INT64_MIN / -1 without host overflow.
            yield return ((BigInteger.One << (width * 2 - 1)) - 1, 1);
            yield return (BigInteger.One << width, 2); // Positive quotient == sign bit.
            yield return ((sign - 1) * 3 + 2, 3);
            yield return (-((sign - 1) * 3 + 2), 3);
            yield return (-sign * 3 - 2, 3); // Minimum valid quotient, negative remainder.
            yield return (-sign, -sign);
            yield return (sign - 1, -sign); // Zero quotient with a nonzero remainder.
            yield return (sign * 3, -sign);
            yield return (-sign * 3, -sign);
            yield return (0, 0);
            yield return (-sign, 0);
        }
    }

    private static void SetDoubleWidthExpected(uint[] registers, int width, BigInteger bits)
    {
        uint low = (uint)(bits & IntegerOracle.Mask(width));
        uint high = (uint)((bits >> width) & IntegerOracle.Mask(width));
        if (width == 8)
        {
            registers[(int)Reg.EAX] = IntegerOracle.PutLane(registers[(int)Reg.EAX], OperandLane.Ax, (uint)bits);
        }
        else
        {
            OperandLane lane = width == 16 ? OperandLane.Ax : OperandLane.Eax;
            registers[(int)Reg.EAX] = IntegerOracle.PutLane(registers[(int)Reg.EAX], lane, low);
            registers[(int)Reg.EDX] = IntegerOracle.PutLane(registers[(int)Reg.EDX], lane, high);
        }
    }

    private static void SetDivisionExpected(uint[] registers, int width, BigInteger quotient, BigInteger remainder)
    {
        uint low = (uint)IntegerOracle.BitPattern(quotient, width);
        uint high = (uint)IntegerOracle.BitPattern(remainder, width);
        if (width == 8)
        {
            registers[(int)Reg.EAX] = IntegerOracle.PutLane(registers[(int)Reg.EAX], OperandLane.Ax, low | (high << 8));
        }
        else
        {
            OperandLane lane = width == 16 ? OperandLane.Ax : OperandLane.Eax;
            registers[(int)Reg.EAX] = IntegerOracle.PutLane(registers[(int)Reg.EAX], lane, low);
            registers[(int)Reg.EDX] = IntegerOracle.PutLane(registers[(int)Reg.EDX], lane, high);
        }
    }
}
