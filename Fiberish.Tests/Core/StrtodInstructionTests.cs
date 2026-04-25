using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class StrtodInstructionTests
{
    private const uint CodeAddr = 0x46100000;
    private const uint DataAddr = 0x46101000;

    private const uint CarryFlag = 1u << 0;
    private const uint ParityFlag = 1u << 2;
    private const uint ZeroFlag = 1u << 6;
    private const uint CompareFlagMask = CarryFlag | ParityFlag | ZeroFlag;
    private const ushort X87CompareStatusMask = 0x4500; // C3/C2/C0

    [Fact]
    public void StrtodStyleFcom_NaN_SetsUnorderedFlagsInStatusWordAndEflags()
    {
        var nan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));

        var result = ExecuteSequence(BuildCompareSequence(0xD8, 0xD1), BitConverter.GetBytes(nan));

        Assert.Equal(X87CompareStatusMask, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal(CompareFlagMask, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void StrtodStyleFucom_NaN_SetsUnorderedFlagsInStatusWordAndEflags()
    {
        var nan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));

        var result = ExecuteSequence(BuildCompareSequence(0xDD, 0xE1), BitConverter.GetBytes(nan));

        Assert.Equal(X87CompareStatusMask, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal(CompareFlagMask, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void StrtodStyleFucom_Zero_SetsEqualFlagsViaFnstswAndSahf()
    {
        var result = ExecuteSequence(BuildCompareSequence(0xDD, 0xE1), BitConverter.GetBytes(0.0));

        Assert.Equal((ushort)0x4000, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal(ZeroFlag, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void StrtodStyleFucom_PositiveFinite_ClearsCompareFlagsViaFnstswAndSahf()
    {
        var result = ExecuteSequence(BuildCompareSequence(0xDD, 0xE1), BitConverter.GetBytes(1.25));

        Assert.Equal((ushort)0x0000, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal(0u, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void FldM64NaN_FstpM64_RoundTripsAsNaN()
    {
        var nan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));
        var stored = ExecuteLoadStoreSequence(BitConverter.GetBytes(nan));
        var bits = BitConverter.ToUInt64(stored);

        Assert.True(double.IsNaN(BitConverter.ToDouble(stored)), $"stored=0x{bits:X16}");
    }

    [Fact]
    public void FdivZeroByZero_FstpM64_RoundTripsAsNaN()
    {
        var stored = ExecuteStoreSequence(
            [
                0xD9, 0xEE, // fldz
                0xD9, 0xEE, // fldz
                0xDE, 0xF9, // fdivp st(1), st
                0xDD, 0x1D, 0x00, 0x00, 0x00, 0x00 // fstp qword ptr [imm32]
            ], []);
        var bits = BitConverter.ToUInt64(stored);

        Assert.True(double.IsNaN(BitConverter.ToDouble(stored)), $"stored=0x{bits:X16}");
    }

    [Fact]
    public void FsqrtNegativeOne_FstpM64_RoundTripsAsNaN()
    {
        var stored = ExecuteStoreSequence(
            [
                0xDD, 0x05, 0x00, 0x00, 0x00, 0x00, // fld qword ptr [imm32]
                0xD9, 0xFA, // fsqrt
                0xDD, 0x1D, 0x00, 0x00, 0x00, 0x00 // fstp qword ptr [imm32]
            ], BitConverter.GetBytes(-1.0));
        var bits = BitConverter.ToUInt64(stored);

        Assert.True(double.IsNaN(BitConverter.ToDouble(stored)), $"stored=0x{bits:X16}");
    }

    [Fact]
    public void FcomQNaN_SetsInvalidAndUnorderedFlags()
    {
        var qnan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));
        var result = ExecuteSequence(BuildCompareSequence(0xD8, 0xD1), BitConverter.GetBytes(qnan));

        Assert.Equal(X87CompareStatusMask, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal((ushort)0x0001, (ushort)(result.Ax & 0x0001));
        Assert.Equal(CompareFlagMask, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void FucomQNaN_DoesNotSetInvalidButStaysUnordered()
    {
        var qnan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));
        var result = ExecuteSequence(BuildCompareSequence(0xDD, 0xE1), BitConverter.GetBytes(qnan));

        Assert.Equal(X87CompareStatusMask, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal((ushort)0x0000, (ushort)(result.Ax & 0x0001));
        Assert.Equal(CompareFlagMask, result.Eflags & CompareFlagMask);
    }

    [Fact]
    public void FucomSNaN_SetsInvalidAndUnorderedFlags()
    {
        var snan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff0000000000001UL));
        var result = ExecuteSequence(BuildCompareSequence(0xDD, 0xE1), BitConverter.GetBytes(snan));

        Assert.Equal(X87CompareStatusMask, (ushort)(result.Ax & X87CompareStatusMask));
        Assert.Equal((ushort)0x0001, (ushort)(result.Ax & 0x0001));
        Assert.Equal(CompareFlagMask, result.Eflags & CompareFlagMask);
    }

    private static byte[] BuildCompareSequence(byte compareOpcode0, byte compareOpcode1)
    {
        // Mirrors the status-transfer pattern used by musl strtod:
        //   fldz; fld [mem]; fcom/fucom st(1); fnstsw ax; fstp st(1); sahf
        byte[] instruction = new byte[15];
        var offset = 0;

        instruction[offset++] = 0xD9;
        instruction[offset++] = 0xEE; // fldz

        instruction[offset++] = 0xDD;
        instruction[offset++] = 0x05; // fld qword ptr [imm32]
        BinaryPrimitives.WriteUInt32LittleEndian(instruction.AsSpan(offset, 4), DataAddr);
        offset += 4;

        instruction[offset++] = compareOpcode0;
        instruction[offset++] = compareOpcode1;

        instruction[offset++] = 0xDF;
        instruction[offset++] = 0xE0; // fnstsw ax

        instruction[offset++] = 0xDD;
        instruction[offset++] = 0xD9; // fstp st(1)

        instruction[offset] = 0x9E; // sahf

        return instruction;
    }

    private static (ushort Ax, uint Eflags) ExecuteSequence(byte[] instruction, byte[] payload)
    {
        var runtime = new MemoryRuntimeContext();
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            mm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write | Protection.Exec,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "strtod-x87-code", engine));
        Assert.Equal(DataAddr,
            mm.Mmap(DataAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "strtod-x87-data", engine));

        Assert.True(engine.CopyToUser(CodeAddr, instruction));
        Assert.True(engine.CopyToUser(DataAddr, payload));

        engine.RegWrite(Reg.EAX, 0);
        engine.Eip = CodeAddr;

        var endEip = CodeAddr + (uint)instruction.Length;
        while (engine.Eip < endEip)
        {
            var stepResult = engine.Step();
            Assert.True(stepResult != (int)EmuStatus.Fault,
                $"fault vector={engine.FaultVector} eip=0x{engine.Eip:X8} eflags=0x{engine.Eflags:X8}");
        }

        Assert.Equal(endEip, engine.Eip);
        return ((ushort)engine.RegRead(Reg.EAX), engine.Eflags);
    }

    private static byte[] ExecuteLoadStoreSequence(byte[] payload)
    {
        return ExecuteStoreSequence(
            [
                0xDD, 0x05, 0x00, 0x00, 0x00, 0x00, // fld qword ptr [imm32]
                0xDD, 0x1D, 0x00, 0x00, 0x00, 0x00 // fstp qword ptr [imm32]
            ], payload);
    }

    private static byte[] ExecuteStoreSequence(byte[] instruction, byte[] payload)
    {
        var patched = (byte[])instruction.Clone();

        for (var i = 0; i <= patched.Length - 6; i++)
        {
            if (patched[i] == 0xDD && patched[i + 1] == 0x05)
                BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(i + 2, 4), DataAddr);
            else if (patched[i] == 0xDD && patched[i + 1] == 0x1D)
                BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(i + 2, 4), DataAddr + 8);
        }

        var runtime = new MemoryRuntimeContext();
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            mm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write | Protection.Exec,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "strtod-x87-code", engine));
        Assert.Equal(DataAddr,
            mm.Mmap(DataAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "strtod-x87-data", engine));
        Assert.True(mm.HandleFault(DataAddr + 8, true, engine));

        Assert.True(engine.CopyToUser(CodeAddr, patched));
        if (payload.Length != 0)
            Assert.True(engine.CopyToUser(DataAddr, payload));

        engine.Eip = CodeAddr;
        var endEip = CodeAddr + (uint)patched.Length;
        while (engine.Eip < endEip)
        {
            var stepResult = engine.Step();
            Assert.True(stepResult != (int)EmuStatus.Fault,
                $"fault vector={engine.FaultVector} eip=0x{engine.Eip:X8} eflags=0x{engine.Eflags:X8}");
        }

        var stored = new byte[8];
        Assert.True(engine.CopyFromUser(DataAddr + 8, stored));
        return stored;
    }
}
