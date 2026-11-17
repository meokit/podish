using System.Numerics;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

/// <summary>
/// A rejected RMW store must not turn its output carry into the input carry of
/// the SMC rerun. Expected values/defined flags come from a separate scalar model,
/// not from running another instruction through the same emulator.
/// </summary>
public sealed class CarryRmwSmcTests
{
    private const uint ArithmeticFlags = 0x8D5;
    private const uint InitialFlags = 0xED6; // OF/SF/ZF/AF/PF, DF/IF, reserved bit 1; CF clear
    private const uint AliasAddress = 0x00900000;

    public static IEnumerable<object[]> Cases()
    {
        foreach (string location in new[] { "data", "code", "cross-code", "alias" })
        foreach (int width in new[] { 8, 16, 32 })
        {
            if (location == "cross-code" && width == 8) continue;
            foreach (bool carry in new[] { false, true })
            {
                foreach (string kind in new[] { "adc", "sbb" })
                foreach (string form in width == 8 ? new[] { "reg", "imm" } : new[] { "reg", "imm", "imm8" })
                    yield return [kind, width, form, 1, carry, location];

                foreach (string kind in new[] { "rcl", "rcr" })
                {
                    yield return [kind, width, "one", 1, carry, location];
                    foreach (int count in new[] { 0, 1, 2, width + 1, 31, 32, 33 }.Distinct())
                    foreach (string form in new[] { "imm", "cl" })
                        yield return [kind, width, form, count, carry, location];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MemoryRmw_CommitsOnceWithOriginalInputFlags(
        string kind, int width, string form, int count, bool carry, string location)
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        cpu.MapExternalMemory(CpuSandbox.DataAddress, 3);
        if (location == "cross-code") cpu.MapExternalMemory(CpuSandbox.CodeAddress + CpuSandbox.PageSize, 7);
        if (location == "alias") cpu.MapExternalAlias(AliasAddress, CpuSandbox.CodeAddress);
        uint target = location switch
        {
            "data" => CpuSandbox.DataAddress + 0x100,
            "cross-code" => CpuSandbox.CodeAddress + CpuSandbox.PageSize - 1,
            "alias" => AliasAddress + 0x100,
            _ => CpuSandbox.CodeAddress + 0x100
        };

        var (oldValue, source) = Operands(kind, width, carry);
        WriteValue(cpu, target, oldValue, width);
        byte[] code = Encode(kind, width, form, count, source);
        cpu.WriteCode(code);
        cpu[Reg.EDI] = target;
        cpu[Reg.ECX] = kind is "adc" or "sbb" ? source : (uint)count;
        uint inputFlags = InitialFlags | (carry ? 1u : 0u);
        cpu.Eflags = inputFlags;
        var (expected, expectedFlags, flagMask) = Evaluate(kind, width, oldValue, source, count, inputFlags);

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length, maxInstructions: 1);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress + (uint)code.Length, cpu.Eip);
        Assert.Equal(expected, ReadValue(cpu, target, width));
        Assert.Equal(expectedFlags & flagMask, cpu.Eflags & flagMask);
        Assert.Equal(inputFlags & 0x600u, cpu.Eflags & 0x600u); // unrelated IF/DF survive
        Assert.Equal(target, cpu[Reg.EDI]);
    }

    public static IEnumerable<object[]> FaultCases()
    {
        foreach (string kind in new[] { "adc", "sbb", "rcl", "rcr" })
        foreach (int width in new[] { 8, 16, 32 })
        foreach (bool crossPage in new[] { false, true })
        {
            if (crossPage && width == 8) continue;
            yield return [kind, width, crossPage];
        }
    }

    [Theory]
    [MemberData(nameof(FaultCases))]
    public void WriteFault_PreservesMemoryAndInstructionInputFlags(string kind, int width, bool crossPage)
    {
        using var cpu = new CpuSandbox();
        cpu.MapMemory(CpuSandbox.DataAddress + CpuSandbox.PageSize, CpuSandbox.PageSize);
        uint target = CpuSandbox.DataAddress + (crossPage ? CpuSandbox.PageSize - 1 : 0x100);
        const uint oldValue = 2;
        WriteValue(cpu, target, oldValue, width);
        uint protectedPage = CpuSandbox.DataAddress + (crossPage ? CpuSandbox.PageSize : 0);
        cpu.ProtectMemory(protectedPage, CpuSandbox.PageSize, 1);
        byte[] code = Encode(kind, width, kind is "adc" or "sbb" ? "reg" : "one", 1);
        cpu.WriteCode(code);
        cpu[Reg.EDI] = target;
        cpu[Reg.ECX] = 0;
        uint inputFlags = InitialFlags | 1u;
        cpu.Eflags = inputFlags;

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(oldValue, ReadValue(cpu, target, width));
        Assert.Equal(inputFlags & ArithmeticFlags, cpu.Eflags & ArithmeticFlags);
    }

    [Fact]
    public void WriteFault_PreservesLazyFlagsFromEarlierInstruction()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteMemory(CpuSandbox.DataAddress, [0x02]);
        cpu.ProtectMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize, 1);
        // cmp eax,eax -> ZF/PF set, CF clear; adc byte [edi],cl faults.
        // setz after ADC keeps its flags live through DFE (but is never reached).
        byte[] code = [0x39, 0xC0, 0x10, 0x0F, 0x0F, 0x94, 0xC2];
        cpu.WriteCode(code);
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu[Reg.ECX] = 1;
        cpu.Eflags = 0x203;

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress + 2, cpu.Eip);
        Assert.Equal(0x44u, cpu.Eflags & ArithmeticFlags);
        Assert.Equal((byte)2, cpu.ReadMemory(CpuSandbox.DataAddress, 1)[0]);
    }

    [Theory]
    [InlineData("adc", 16)]
    [InlineData("adc", 32)]
    [InlineData("sbb", 16)]
    [InlineData("sbb", 32)]
    public void SignExtendedImmediate_SmcRerunUsesOriginalCarry(string kind, int width)
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        uint target = CpuSandbox.CodeAddress + 0x100;
        uint oldValue = kind == "adc" ? 0u : Mask(width);
        uint inputFlags = InitialFlags | (kind == "adc" ? 1u : 0u);
        WriteValue(cpu, target, oldValue, width);
        byte[] code = Encode(kind, width, "imm8", 1, uint.MaxValue);
        cpu.WriteCode(code);
        cpu[Reg.EDI] = target;
        cpu.Eflags = inputFlags;
        var (expected, flags, mask) = Evaluate(kind, width, oldValue, Mask(width), 1, inputFlags);

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(expected, ReadValue(cpu, target, width));
        Assert.Equal(flags & mask, cpu.Eflags & mask);
    }

    [Theory]
    [InlineData("adc")]
    [InlineData("sbb")]
    public void DeadOutputFlags_StillUsesInputCarryForSmcStore(string kind)
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        uint target = CpuSandbox.CodeAddress + 0x100;
        uint oldValue = kind == "adc" ? 0u : 2u;
        WriteValue(cpu, target, oldValue, 32);
        // The following CMP kills all output flags, selecting the NF handler.
        byte[] code = [.. Encode(kind, 32, "reg", 1), 0x39, 0xD2];
        cpu.WriteCode(code);
        cpu[Reg.EDI] = target;
        cpu[Reg.ECX] = 0;
        cpu.Eflags = InitialFlags | 1;

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(1u, ReadValue(cpu, target, 32));
        Assert.Equal(0x44u, cpu.Eflags & ArithmeticFlags);
    }

    [Theory]
    [InlineData("adc")]
    [InlineData("sbb")]
    [InlineData("rcl")]
    [InlineData("rcr")]
    public void CleanDataPage_FirstRmwStoreMarksDirty(string kind)
    {
        using var cpu = new CpuSandbox();
        Assert.Equal(0, X86Native.IsDirty(cpu.State, CpuSandbox.DataAddress));
        byte[] code = Encode(kind, 32, kind is "adc" or "sbb" ? "reg" : "one", 1);
        cpu.WriteCode(code);
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu[Reg.ECX] = 0;
        cpu.Eflags = 0x203;
        var (expected, flags, mask) = Evaluate(kind, 32, 0, 0, 1, 0x203);

        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(expected, ReadValue(cpu, CpuSandbox.DataAddress, 32));
        Assert.Equal(flags & mask, cpu.Eflags & mask);
        Assert.Equal(1, X86Native.IsDirty(cpu.State, CpuSandbox.DataAddress));
    }

    [Theory]
    [InlineData("adc", 8)]
    [InlineData("adc", 16)]
    [InlineData("adc", 32)]
    [InlineData("sbb", 8)]
    [InlineData("sbb", 16)]
    [InlineData("sbb", 32)]
    public void RegisterOnlySpecialization_PreservesPartialRegistersAndCarry(string kind, int width)
    {
        foreach (bool deadFlags in new[] { false, true })
        {
            using var cpu = new CpuSandbox();
            byte[] instruction = Encode(kind, width, "reg", 1);
            // Change [edi] to BH/DI/EDI without changing the source (CL/CX/ECX).
            instruction[width == 16 ? 2 : 1] |= 0xC0;
            byte[] code = deadFlags ? [.. instruction, 0x39, 0xD2] : instruction;
            cpu.WriteCode(code);
            cpu[Reg.EBX] = 0x12340278;
            cpu[Reg.EDI] = 0x12340002;
            cpu[Reg.ECX] = 0;
            cpu.Eflags = InitialFlags | 1;
            uint oldValue = width == 32 ? 0x12340002u : 2u;
            var (expected, flags, mask) = Evaluate(kind, width, oldValue, 0, 1, InitialFlags | 1);

            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            if (width == 8) Assert.Equal(0x12340078u | (expected << 8), cpu[Reg.EBX]);
            else if (width == 16) Assert.Equal(0x12340000u | expected, cpu[Reg.EDI]);
            else Assert.Equal(expected, cpu[Reg.EDI]);
            Assert.Equal(deadFlags ? 0x44u : flags & mask, cpu.Eflags & mask);
        }
    }

    private static (uint OldValue, uint Source) Operands(string kind, int width, bool carry) => kind switch
    {
        "adc" => carry ? (0u, 0u) : (Mask(width), 1u),
        "sbb" => carry ? (2u, 0u) : (0u, 1u),
        "rcl" => (carry ? 0u : 1u << (width - 1), 0u),
        _ => (carry ? 0u : 1u, 0u)
    };

    private static byte[] Encode(string kind, int width, string form, int count, uint source = 0)
    {
        List<byte> code = [];
        if (width == 16) code.Add(0x66);
        bool arithmetic = kind is "adc" or "sbb";
        int subop = kind is "adc" or "rcl" ? 2 : 3;
        if (arithmetic && form == "reg")
        {
            code.Add((byte)((kind == "adc" ? 0x10 : 0x18) + (width == 8 ? 0 : 1)));
            code.Add(0x0F); // [edi], cl/cx/ecx
        }
        else if (arithmetic)
        {
            code.Add(width == 8 ? (byte)0x80 : form == "imm8" ? (byte)0x83 : (byte)0x81);
            code.Add((byte)((subop << 3) | 7)); // [edi]
            code.Add((byte)source);
            if (width != 8 && form != "imm8")
                for (int i = 1; i < width / 8; i++) code.Add((byte)(source >> (i * 8)));
        }
        else
        {
            code.Add((byte)((form == "one" ? 0xD0 : form == "cl" ? 0xD2 : 0xC0) + (width == 8 ? 0 : 1)));
            code.Add((byte)((subop << 3) | 7));
            if (form == "imm") code.Add((byte)count);
        }
        return code.ToArray();
    }

    private static (uint Value, uint Flags, uint FlagMask) Evaluate(
        string kind, int width, uint a, uint b, int count, uint flags)
    {
        uint mask = Mask(width);
        uint sign = 1u << (width - 1);
        uint carry = flags & 1;
        if (kind is "adc" or "sbb")
        {
            ulong wide = kind == "adc" ? (ulong)a + b + carry : unchecked((ulong)a - b - carry);
            uint result = (uint)wide & mask;
            bool cf = kind == "adc" ? wide > mask : (ulong)a < (ulong)b + carry;
            bool of = kind == "adc" ? (~(a ^ b) & (a ^ result) & sign) != 0 : ((a ^ b) & (a ^ result) & sign) != 0;
            flags &= ~ArithmeticFlags;
            if (cf) flags |= 1;
            if (BitOperations.PopCount(result & 0xFF) % 2 == 0) flags |= 4;
            if (((a ^ b ^ result) & 0x10) != 0) flags |= 0x10;
            if (result == 0) flags |= 0x40;
            if ((result & sign) != 0) flags |= 0x80;
            if (of) flags |= 0x800;
            return (result, flags, ArithmeticFlags);
        }

        int masked = count & 31;
        int effective = masked % (width + 1);
        if (effective == 0) return (a, flags, ArithmeticFlags);
        ulong ring = a | ((ulong)carry << width);
        ulong ringMask = (1UL << (width + 1)) - 1;
        ring = kind == "rcl"
            ? ((ring << effective) | (ring >> (width + 1 - effective))) & ringMask
            : ((ring >> effective) | (ring << (width + 1 - effective))) & ringMask;
        uint rotated = (uint)ring & mask;
        uint newCarry = (uint)(ring >> width);
        flags = (flags & ~1u) | newCarry;
        if (masked == 1)
        {
            uint overflow = kind == "rcl"
                ? ((rotated >> (width - 1)) ^ newCarry) & 1
                : ((rotated >> (width - 1)) ^ (rotated >> (width - 2))) & 1;
            flags = (flags & ~0x800u) | (overflow << 11);
        }
        return (rotated, flags, masked == 1 ? ArithmeticFlags : ArithmeticFlags & ~0x800u);
    }

    private static uint Mask(int width) => width == 32 ? uint.MaxValue : (1u << width) - 1;

    private static void WriteValue(CpuSandbox cpu, uint address, uint value, int width) =>
        cpu.WriteMemory(address, BitConverter.GetBytes(value).AsSpan(0, width / 8));

    private static uint ReadValue(CpuSandbox cpu, uint address, int width)
    {
        byte[] bytes = new byte[4];
        cpu.ReadMemory(address, width / 8).CopyTo(bytes, 0);
        return BitConverter.ToUInt32(bytes);
    }
}
