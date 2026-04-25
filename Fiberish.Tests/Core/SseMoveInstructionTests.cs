using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class SseMoveInstructionTests
{
    private const uint CodeAddr = 0x46200000;
    private const uint DataAddr = 0x46201000;
    private const uint SrcAddr = DataAddr + 0x10;
    private const uint OutAddr = DataAddr + 0x20;

    [Fact]
    public void Movlhps_RegReg_CopiesSourceLowQwordIntoDestinationHighQword()
    {
        var stored = ExecuteSequence(BuildSequence(0x16), CreateVector(0x11111111, 0x22222222, 0x33333333, 0x44444444),
            CreateVector(0xAAAAAAAA, 0xBBBBBBBB, 0xCCCCCCCC, 0xDDDDDDDD));

        Assert.Equal(new uint[] { 0x11111111, 0x22222222, 0xAAAAAAAA, 0xBBBBBBBB }, ReadVector(stored));
    }

    [Fact]
    public void Movhlps_RegReg_CopiesSourceHighQwordIntoDestinationLowQword()
    {
        var stored = ExecuteSequence(BuildSequence(0x12), CreateVector(0x11111111, 0x22222222, 0x33333333, 0x44444444),
            CreateVector(0xAAAAAAAA, 0xBBBBBBBB, 0xCCCCCCCC, 0xDDDDDDDD));

        Assert.Equal(new uint[] { 0xCCCCCCCC, 0xDDDDDDDD, 0x33333333, 0x44444444 }, ReadVector(stored));
    }

    private static byte[] BuildSequence(byte opcode)
    {
        var code = new byte[24];
        var offset = 0;

        code[offset++] = 0x0F;
        code[offset++] = 0x10;
        code[offset++] = 0x05; // movups xmm0, [DataAddr]
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(offset, 4), DataAddr);
        offset += 4;

        code[offset++] = 0x0F;
        code[offset++] = 0x10;
        code[offset++] = 0x0D; // movups xmm1, [SrcAddr]
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(offset, 4), SrcAddr);
        offset += 4;

        code[offset++] = 0x0F;
        code[offset++] = opcode;
        code[offset++] = 0xC1; // xmm0, xmm1

        code[offset++] = 0x0F;
        code[offset++] = 0x11;
        code[offset++] = 0x05; // movups [OutAddr], xmm0
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(offset, 4), OutAddr);

        return code;
    }

    private static byte[] ExecuteSequence(byte[] instruction, byte[] dstPayload, byte[] srcPayload)
    {
        var runtime = new MemoryRuntimeContext();
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            mm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write | Protection.Exec,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "sse-mov-code", engine));
        Assert.Equal(DataAddr,
            mm.Mmap(DataAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "sse-mov-data", engine));

        Assert.True(engine.CopyToUser(CodeAddr, instruction));
        Assert.True(engine.CopyToUser(DataAddr, dstPayload));
        Assert.True(engine.CopyToUser(SrcAddr, srcPayload));

        engine.Eip = CodeAddr;
        var endEip = CodeAddr + (uint)instruction.Length;
        while (engine.Eip < endEip)
        {
            var stepResult = engine.Step();
            Assert.True(stepResult != (int)EmuStatus.Fault,
                $"fault vector={engine.FaultVector} eip=0x{engine.Eip:X8}");
        }

        var stored = new byte[16];
        Assert.True(engine.CopyFromUser(OutAddr, stored));
        return stored;
    }

    private static byte[] CreateVector(params uint[] lanes)
    {
        Assert.Equal(4, lanes.Length);
        var bytes = new byte[16];
        for (var i = 0; i < lanes.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), lanes[i]);
        return bytes;
    }

    private static uint[] ReadVector(byte[] bytes)
    {
        Assert.Equal(16, bytes.Length);
        var lanes = new uint[4];
        for (var i = 0; i < lanes.Length; i++)
            lanes[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        return lanes;
    }
}
