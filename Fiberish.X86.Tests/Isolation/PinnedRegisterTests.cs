using System.Runtime.InteropServices;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class PinnedRegisterTests
{
    [Fact]
    public void EveryRegisterAndByteLaneRemainsVisibleToFollowingInstructions()
    {
        foreach (int width in new[] { 8, 16, 32 })
        foreach (bool reverseEncoding in new[] { false, true })
        for (int destination = 0; destination < 8; destination++)
        for (int source = 0; source < 8; source++)
        {
            using var cpu = new CpuSandbox();
            uint[] registers = Enumerable.Range(0, 8).Select(i => 0x12345678u + (uint)i * 0x1010101u).ToArray();
            for (int i = 0; i < 8; i++) cpu[(Reg)i] = registers[i];
            int parent = width == 8 ? destination & 3 : destination;
            int sourceParent = width == 8 ? source & 3 : source;
            int shift = width == 8 && destination >= 4 ? 8 : 0;
            int sourceShift = width == 8 && source >= 4 ? 8 : 0;
            uint mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
            uint value = (registers[sourceParent] >> sourceShift) & mask;
            registers[parent] = (registers[parent] & ~(mask << shift)) | (value << shift);
            var code = new List<byte>();
            if (width == 16) code.Add(0x66);
            code.Add((byte)((width == 8 ? 0x88 : 0x89) + (reverseEncoding ? 2 : 0)));
            code.Add((byte)(0xC0 | ((reverseEncoding ? destination : source) << 3) |
                (reverseEncoding ? source : destination)));
            // Read both cached registers inside the dispatch chain, before the C ABI exit.
            code.AddRange(new byte[] { 0xA3, 0, 0, 0x20, 0, 0x89, 0x25, 4, 0, 0x20, 0 });
            cpu.WriteCode(code.ToArray());
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Count);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(registers[0], BitConverter.ToUInt32(cpu.ReadMemory(CpuSandbox.DataAddress, 4)));
            Assert.Equal(registers[4], BitConverter.ToUInt32(cpu.ReadMemory(CpuSandbox.DataAddress + 4, 4)));
            for (int i = 0; i < 8; i++) Assert.Equal(registers[i], cpu[(Reg)i]);
        }
    }

    [Fact]
    public void RotateAndByteGroupWritesRefreshOnlyTheirArchitecturalParent()
    {
        foreach (int width in new[] { 8, 16, 32 })
        for (int destination = 0; destination < 8; destination++)
        foreach (int count in new[] { 0, 1, 7, 31 })
        {
            using var cpu = new CpuSandbox();
            uint[] registers = Enumerable.Range(0, 8).Select(i => 0x12345678u + (uint)i * 0x1010101u).ToArray();
            for (int i = 0; i < 8; i++) cpu[(Reg)i] = registers[i];
            int parent = width == 8 ? destination & 3 : destination;
            int shift = width == 8 && destination >= 4 ? 8 : 0;
            uint mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
            uint value = (registers[parent] >> shift) & mask;
            var code = new List<byte>();
            if (width == 8)
            {
                // XOR byte register, imm8: AL/AH both invalidate EAX, never ESP.
                code.AddRange(new byte[] { 0x80, (byte)(0xF0 | destination), (byte)count });
                value ^= (uint)count;
            }
            else
            {
                if (width == 16) code.Add(0x66);
                code.AddRange(new byte[] { 0xC1, (byte)(0xC8 | destination), (byte)count });
                int rotation = count % width;
                if (rotation != 0) value = ((value >> rotation) | (value << (width - rotation))) & mask;
            }
            registers[parent] = (registers[parent] & ~(mask << shift)) | (value << shift);
            code.AddRange(new byte[] { 0xA3, 0, 0, 0x20, 0, 0x89, 0x25, 4, 0, 0x20, 0 });
            cpu.WriteCode(code.ToArray());
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Count);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(registers[0], BitConverter.ToUInt32(cpu.ReadMemory(CpuSandbox.DataAddress, 4)));
            Assert.Equal(registers[4], BitConverter.ToUInt32(cpu.ReadMemory(CpuSandbox.DataAddress + 4, 4)));
            for (int i = 0; i < 8; i++) Assert.Equal(registers[i], cpu[(Reg)i]);
        }
    }

    [Fact]
    public void ApiWritesAreReloadedWhenReenteringAnAlreadyDecodedBlock()
    {
        using var cpu = new CpuSandbox();
        byte[] code = [0x89, 0xC2, 0x89, 0xE3]; // mov edx,eax; mov ebx,esp
        cpu.WriteCode(code);
        for (uint i = 1; i <= 4; i++)
        {
            cpu[Reg.EAX] = 0x12340000u + i;
            cpu[Reg.ESP] = 0x56780000u + i;
            cpu.Eip = CpuSandbox.CodeAddress;
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            Assert.Equal(cpu[Reg.EAX], cpu[Reg.EDX]);
            Assert.Equal(cpu[Reg.ESP], cpu[Reg.EBX]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void FaultCallbackCanObserveAndReplaceBothCachedRegisters(bool resolve)
    {
        using var cpu = new CpuSandbox();
        cpu.UnmapMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize);
        cpu[Reg.EAX] = 0x11112222;
        cpu[Reg.ESP] = 0x33334444;
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        // Writes happen before the callback; reads happen after the slow-path resume.
        byte[] code = [0x83, 0xC0, 1, 0x83, 0xC4, 2, 0x8B, 0x0F, 0x89, 0xC2, 0x89, 0xE3];
        cpu.WriteCode(code);
        var context = new FaultContext(cpu, resolve);
        var root = GCHandle.Alloc(context);
        try
        {
            X86Native.SetFaultCallback(cpu.State, &OnFault, GCHandle.ToIntPtr(root));
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            Assert.Null(context.Error);
            Assert.Equal(1, context.Invocations);
            Assert.Equal(0xAABBCCDDu, cpu[Reg.EAX]);
            Assert.Equal(0x12345678u, cpu[Reg.ESP]);
            if (!resolve)
            {
                Assert.Equal(EmuStatus.Fault, cpu.Status);
                Assert.Equal(CpuSandbox.CodeAddress + 6, cpu.Eip);
                cpu.MapMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize);
                cpu.WriteMemory(CpuSandbox.DataAddress, BitConverter.GetBytes(0x99887766u));
                X86Native.SetFaultCallback(cpu.State, null, IntPtr.Zero);
                cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            }
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(0xAABBCCDDu, cpu[Reg.EDX]);
            Assert.Equal(0x12345678u, cpu[Reg.EBX]);
            Assert.Equal(0x99887766u, cpu[Reg.ECX]);
        }
        finally
        {
            X86Native.SetFaultCallback(cpu.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnFault(IntPtr state, uint address, int isWrite, IntPtr userdata)
    {
        var context = (FaultContext)GCHandle.FromIntPtr(userdata).Target!;
        try
        {
            Assert.Equal(context.Cpu.State, state);
            Assert.Equal(CpuSandbox.DataAddress, address);
            Assert.Equal(0, isWrite);
            Assert.Equal(0x11112223u, context.Cpu[Reg.EAX]);
            Assert.Equal(0x33334446u, context.Cpu[Reg.ESP]);
            context.Invocations++;
            context.Cpu[Reg.EAX] = 0xAABBCCDD;
            context.Cpu[Reg.ESP] = 0x12345678;
            if (!context.Resolve) return 0;
            context.Cpu.MapMemory(address, CpuSandbox.PageSize);
            context.Cpu.WriteMemory(address, BitConverter.GetBytes(0x99887766u));
            return 1;
        }
        catch (Exception error)
        {
            context.Error = error;
            X86Native.EmuFault(state);
            return 0;
        }
    }

    private sealed class FaultContext(CpuSandbox cpu, bool resolve)
    {
        public CpuSandbox Cpu { get; } = cpu;
        public bool Resolve { get; } = resolve;
        public int Invocations { get; set; }
        public Exception? Error { get; set; }
    }
}
