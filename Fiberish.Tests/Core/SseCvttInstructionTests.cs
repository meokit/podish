using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class SseCvttInstructionTests
{
    private const uint CodeAddr = 0x46000000;
    private const uint DataAddr = 0x46001000;

    [Fact]
    public void Cvttsd2si_FiniteDoubleMemoryOperand_TruncatesTowardZero()
    {
        ExecuteCvttsd2si(BitConverter.GetBytes(42.9), 42u);
    }

    [Fact]
    public void Cvttsd2si_NaNDoubleMemoryOperand_ReturnsIndefiniteInteger()
    {
        var nan = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000000UL));
        ExecuteCvttsd2si(BitConverter.GetBytes(nan), unchecked((uint)int.MinValue));
    }

    [Fact]
    public void Cvttss2si_FiniteSingleMemoryOperand_TruncatesTowardZero()
    {
        ExecuteCvttss2si(BitConverter.GetBytes(42.9f), 42u);
    }

    [Fact]
    public void Cvttss2si_NaNSingleMemoryOperand_ReturnsIndefiniteInteger()
    {
        var nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00000U));
        ExecuteCvttss2si(BitConverter.GetBytes(nan), unchecked((uint)int.MinValue));
    }

    private static void ExecuteCvttsd2si(byte[] payload, uint expectedEax)
    {
        byte[] instruction = new byte[8];
        instruction[0] = 0xF2;
        instruction[1] = 0x0F;
        instruction[2] = 0x2C;
        instruction[3] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(instruction.AsSpan(4), DataAddr);

        ExecuteSingleInstruction(instruction, payload, expectedEax);
    }

    private static void ExecuteCvttss2si(byte[] payload, uint expectedEax)
    {
        byte[] instruction = new byte[8];
        instruction[0] = 0xF3;
        instruction[1] = 0x0F;
        instruction[2] = 0x2C;
        instruction[3] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(instruction.AsSpan(4), DataAddr);

        ExecuteSingleInstruction(instruction, payload, expectedEax);
    }

    private static void ExecuteSingleInstruction(byte[] instruction, byte[] payload, uint expectedEax)
    {
        var runtime = new MemoryRuntimeContext();
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            mm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write | Protection.Exec,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "sse-cvtt-code", engine));
        Assert.Equal(DataAddr,
            mm.Mmap(DataAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "sse-cvtt-data", engine));

        Assert.True(engine.CopyToUser(CodeAddr, instruction));
        Assert.True(engine.CopyToUser(DataAddr, payload));

        engine.RegWrite(Reg.EAX, 0xDEADBEEF);
        engine.Eip = CodeAddr;

        var stepResult = engine.Step();

        Assert.NotEqual((int)EmuStatus.Fault, stepResult);
        Assert.Equal(CodeAddr + (uint)instruction.Length, engine.Eip);
        Assert.Equal(expectedEax, engine.RegRead(Reg.EAX));
    }
}
