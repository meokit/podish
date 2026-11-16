using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fiberish.X86.Tests.Integer;

public enum BinaryOperation { Add, Adc, Sub, Sbb, And, Or, Xor, Cmp, Test }
public enum UnaryOperation { Inc, Dec, Neg, Not }
public enum ShiftOperation { Rol, Ror, Rcl, Rcr, Shl, Shr, Sar }
public enum OperandLane { Al, Ah, Ax, Eax }
public enum ExecutionMode { Step, Run }
public enum BinaryEncoding { Register, Immediate }
public enum ShiftEncoding { Immediate, Cl, One }

internal readonly record struct IntegerResult(uint Value, uint Flags, uint FlagMask);

// Deliberately uses mathematical, bounded integers, not the emulator's lazy-flag
// representation. Undefined architectural flags are excluded per input vector.
internal static class IntegerOracle
{
    internal const uint Cf = 0x001;
    internal const uint Pf = 0x004;
    internal const uint Af = 0x010;
    internal const uint Zf = 0x040;
    internal const uint Sf = 0x080;
    internal const uint Of = 0x800;
    internal const uint ArithmeticFlags = Cf | Pf | Af | Zf | Sf | Of;

    internal static int Width(OperandLane lane) => lane switch
    {
        OperandLane.Al or OperandLane.Ah => 8,
        OperandLane.Ax => 16,
        OperandLane.Eax => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(lane))
    };

    internal static uint Mask(int width) => (uint)((1UL << width) - 1);
    internal static uint Sign(int width) => 1u << (width - 1);

    internal static long Signed(uint value, int width)
    {
        value &= Mask(width);
        return (value & Sign(width)) == 0 ? value : (long)value - (1L << width);
    }

    internal static uint PutLane(uint container, OperandLane lane, uint value)
    {
        int shift = lane == OperandLane.Ah ? 8 : 0;
        uint mask = Mask(Width(lane));
        return (container & ~(mask << shift)) | ((value & mask) << shift);
    }

    internal static uint InitialFlags(bool set) => 0x202u | (set ? ArithmeticFlags | 0x400u : 0u);

    internal static IEnumerable<uint> Edges(int width)
    {
        uint sign = Sign(width);
        yield return 0;
        yield return 1;
        yield return 0x0f;
        yield return sign - 1;
        yield return sign;
        yield return Mask(width) - 1;
        yield return Mask(width);
    }

    internal static uint ResultFlags(uint value, int width)
    {
        uint flags = value == 0 ? Zf : 0;
        if ((value & Sign(width)) != 0)
            flags |= Sf;
        int ones = 0;
        for (int bit = 0; bit < 8; ++bit)
            ones += (int)((value >> bit) & 1);
        if ((ones & 1) == 0)
            flags |= Pf;
        return flags;
    }

    internal static IntegerResult Binary(BinaryOperation operation, int width, uint left, uint right, uint initialFlags)
    {
        uint mask = Mask(width);
        left &= mask;
        right &= mask;
        uint carry = (operation is BinaryOperation.Adc or BinaryOperation.Sbb) && (initialFlags & Cf) != 0 ? 1u : 0u;
        uint value;
        uint flags = initialFlags & ~ArithmeticFlags;
        uint validity = uint.MaxValue;
        if (operation is BinaryOperation.And or BinaryOperation.Or or BinaryOperation.Xor or BinaryOperation.Test)
        {
            value = operation switch
            {
                BinaryOperation.Or => left | right,
                BinaryOperation.Xor => left ^ right,
                _ => left & right
            };
            validity &= ~Af; // AF is undefined for logical instructions.
        }
        else
        {
            bool subtract = operation is BinaryOperation.Sub or BinaryOperation.Sbb or BinaryOperation.Cmp;
            long mathematical = subtract ? (long)left - right - carry : (long)left + right + carry;
            value = unchecked((uint)mathematical) & mask;
            bool carryOut = subtract ? (ulong)left < (ulong)right + carry : (ulong)left + right + carry > mask;
            bool auxiliary = subtract ? (left & 15) < (right & 15) + carry : (left & 15) + (right & 15) + carry > 15;
            long signedMathematical = subtract
                ? Signed(left, width) - Signed(right, width) - carry
                : Signed(left, width) + Signed(right, width) + carry;
            long limit = 1L << (width - 1);
            if (carryOut) flags |= Cf;
            if (auxiliary) flags |= Af;
            if (signedMathematical < -limit || signedMathematical >= limit) flags |= Of;
        }
        return new IntegerResult(value, flags | ResultFlags(value, width), validity);
    }

    internal static IntegerResult Unary(UnaryOperation operation, int width, uint input, uint initialFlags)
    {
        input &= Mask(width);
        if (operation == UnaryOperation.Not)
            return new IntegerResult(~input & Mask(width), initialFlags, uint.MaxValue);
        IntegerResult result = operation switch
        {
            UnaryOperation.Inc => Binary(BinaryOperation.Add, width, input, 1, initialFlags),
            UnaryOperation.Dec => Binary(BinaryOperation.Sub, width, input, 1, initialFlags),
            UnaryOperation.Neg => Binary(BinaryOperation.Sub, width, 0, input, initialFlags),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (operation is UnaryOperation.Inc or UnaryOperation.Dec)
            result = result with { Flags = (result.Flags & ~Cf) | (initialFlags & Cf) };
        return result;
    }

    internal static IntegerResult Shift(ShiftOperation operation, int width, uint input, byte rawCount, uint initialFlags)
    {
        uint mask = Mask(width);
        uint sign = Sign(width);
        uint value = input & mask;
        int count = rawCount & 31;
        if (count == 0)
            return new IntegerResult(value, initialFlags, uint.MaxValue);

        bool rotate = operation is ShiftOperation.Rol or ShiftOperation.Ror or ShiftOperation.Rcl or ShiftOperation.Rcr;
        bool throughCarry = operation is ShiftOperation.Rcl or ShiftOperation.Rcr;
        int steps = rotate ? count % (width + (throughCarry ? 1 : 0)) : count;
        bool carry = (initialFlags & Cf) != 0;
        // A complete RCL/RCR ring rotation leaves every bit, including CF, alone.
        if (throughCarry && steps == 0)
            return new IntegerResult(value, initialFlags, uint.MaxValue);

        for (int i = 0; i < steps; ++i)
        {
            bool oldCarry = carry;
            if (operation is ShiftOperation.Rol or ShiftOperation.Rcl or ShiftOperation.Shl)
            {
                carry = (value & sign) != 0;
                value = (value << 1) & mask;
                if ((operation == ShiftOperation.Rol && carry) || (operation == ShiftOperation.Rcl && oldCarry))
                    value |= 1;
            }
            else
            {
                bool oldSign = (value & sign) != 0;
                carry = (value & 1) != 0;
                value >>= 1;
                if ((operation == ShiftOperation.Ror && carry) || (operation == ShiftOperation.Rcr && oldCarry)
                    || (operation == ShiftOperation.Sar && oldSign))
                    value |= sign;
            }
        }

        uint validity = uint.MaxValue;
        uint flags;
        if (rotate)
        {
            // ROL/ROR update CF even when a nonzero masked count is a full turn.
            if (operation == ShiftOperation.Rol) carry = (value & 1) != 0;
            if (operation == ShiftOperation.Ror) carry = (value & sign) != 0;
            flags = initialFlags & ~(Cf | Of);
        }
        else
        {
            flags = (initialFlags & ~ArithmeticFlags) | ResultFlags(value, width);
            validity &= ~Af;
            if ((operation is ShiftOperation.Shl or ShiftOperation.Shr) && count >= width)
                validity &= ~Cf;
        }
        if (carry) flags |= Cf;
        if (count == 1)
        {
            bool overflow = operation switch
            {
                ShiftOperation.Shl or ShiftOperation.Rol or ShiftOperation.Rcl => ((value & sign) != 0) != carry,
                ShiftOperation.Shr => (input & sign) != 0,
                ShiftOperation.Sar => false,
                _ => ((value & sign) != 0) != ((value & (sign >> 1)) != 0)
            };
            if (overflow) flags |= Of;
        }
        else
        {
            validity &= ~Of;
        }
        return new IntegerResult(value, flags, validity);
    }

    internal static bool Condition(int condition, uint flags)
    {
        bool carry = (flags & Cf) != 0;
        bool zero = (flags & Zf) != 0;
        bool sign = (flags & Sf) != 0;
        bool overflow = (flags & Of) != 0;
        bool parity = (flags & Pf) != 0;
        return condition switch
        {
            0 => overflow, 1 => !overflow, 2 => carry, 3 => !carry,
            4 => zero, 5 => !zero, 6 => carry || zero, 7 => !carry && !zero,
            8 => sign, 9 => !sign, 10 => parity, 11 => !parity,
            12 => sign != overflow, 13 => sign == overflow,
            14 => zero || sign != overflow, 15 => !zero && sign == overflow,
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
    }

    internal static BigInteger BitPattern(BigInteger value, int width)
    {
        BigInteger modulus = BigInteger.One << width;
        return ((value % modulus) + modulus) % modulus;
    }
}
