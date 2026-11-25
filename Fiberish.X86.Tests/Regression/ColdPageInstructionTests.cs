using System.Runtime.InteropServices;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class ColdPageInstructionTests
{
    private const uint Data = CpuSandbox.DataAddress;
    private const uint Page = CpuSandbox.PageSize;

    public static IEnumerable<object[]> PageFaultCases()
    {
        (string Instruction, int Access)[] instructions =
        [
            ("push", 1), ("call", 1), ("pusha", 1), ("pusha-cross-page", 1),
            ("popa", 0), ("enter", 1), ("leave", 0), ("lock-inc", 0),
            ("xchg", 0), ("cmpxchg-write", 1), ("movdqa-store", 1), ("movdqa-load", 0),
            ("fstp", 1), ("fistp", 1), ("fstp80", 1), ("fld", 0), ("execute", 2),
            ("fnsave", 1), ("fxsave", 1), ("movntq", 1)
        ];
        foreach (var (instruction, access) in instructions)
        {
            yield return [instruction, access, false];
            yield return [instruction, access, true];
        }
    }

    [Theory]
    [MemberData(nameof(PageFaultCases))]
    public unsafe void ResolvingAColdPagePreservesArchitecturalResults(string instruction, int access, bool suspend)
    {
        using var warm = new CpuSandbox();
        using var cold = new CpuSandbox();
        var initial = Enumerable.Range(0, (int)Page).Select(i => (byte)i).ToArray();
        // This real-valued input avoids x87 behavior dependent on invalid encodings.
        BitConverter.GetBytes(1.5f).CopyTo(initial, 0);
        if (instruction == "execute")
            new byte[] { 0xB8, 0x34, 0x12, 0, 0 }.CopyTo(initial, 0); // mov eax, 0x1234
        var end = Prepare(warm, instruction);
        Assert.Equal(end, Prepare(cold, instruction));
        warm.MapMemory(Data, Page, access == 2 ? (byte)7 : (byte)3);
        warm.WriteMemory(Data, initial);
        var writeProtectionFault = instruction == "cmpxchg-write";
        if (writeProtectionFault)
        {
            cold.WriteMemory(Data, initial);
            cold.ProtectMemory(Data, Page, 1);
        }
        else
        {
            cold.UnmapMemory(Data, Page);
        }

        if (instruction is "fstp" or "fistp" or "fstp80" or "fnsave" or "fxsave" or "movntq")
            cold.Run(CpuSandbox.CodeAddress + 2); // Compare state at the faulting instruction, after the x87 setup.
        var before = cold.Snapshot();
        var fault = new PageFault(cold, initial, access == 2 ? (byte)7 : (byte)3, writeProtectionFault, suspend);
        var lease = GCHandle.Alloc(fault);
        try
        {
            X86Native.SetExtendedFaultCallback(cold.State, &PopulatePage, GCHandle.ToIntPtr(lease));
            warm.Run(end);
            cold.Run(end);
            if (suspend)
            {
                Assert.Equal(EmuStatus.Yield, cold.Status);
                var parked = cold.Snapshot();
                Assert.Equal(before.Eip, parked.Eip);
                Assert.Equal(before.Registers, parked.Registers);
                Assert.Equal(before.Eflags, parked.Eflags);
                Assert.Equal(before.Fcw, parked.Fcw);
                Assert.Equal(before.Fsw, parked.Fsw);
                Assert.Equal(before.Ftw, parked.Ftw);
                Assert.Equal(before.Mxcsr, parked.Mxcsr);
                Assert.Equal(before.Xmm[0], parked.Xmm[0]);
                Assert.Equal(before.Fpu[0], parked.Fpu[0]);
                // PUSHA and multi-part saves may already have written accessible memory.
                fault.Install();
                cold.Run(end);
            }
        }
        finally
        {
            X86Native.SetFaultCallback(cold.State, null, IntPtr.Zero);
            lease.Free();
        }

        Assert.Null(fault.Error);
        var observed = Assert.Single(fault.Accesses);
        Assert.Equal(Data, observed.Address & ~(Page - 1));
        Assert.Equal(access | 4, observed.Flags);
        Assert.Equal(EmuStatus.Stopped, warm.Status);
        Assert.Equal(warm.Status, cold.Status);
        var expected = warm.Snapshot();
        var actual = cold.Snapshot();
        Assert.Equal(expected.Eip, actual.Eip);
        Assert.Equal(expected.Registers, actual.Registers);
        Assert.Equal(expected.Eflags, actual.Eflags);
        Assert.Equal(expected.Fcw, actual.Fcw);
        Assert.Equal(expected.Fsw, actual.Fsw);
        Assert.Equal(expected.Ftw, actual.Ftw);
        Assert.Equal(expected.Xmm[0], actual.Xmm[0]);
        Assert.Equal(expected.Fpu[0], actual.Fpu[0]);
        var affectedBytes = instruction == "pusha-cross-page" ? 2 * Page : Page;
        Assert.Equal(warm.ReadMemory(Data, (int)affectedBytes), cold.ReadMemory(Data, (int)affectedBytes));
    }

    [Theory]
    [InlineData("xchg")]
    [InlineData("add")]
    [InlineData("add-imm")]
    [InlineData("adc")]
    [InlineData("inc-byte")]
    [InlineData("neg")]
    [InlineData("bts")]
    [InlineData("shld")]
    [InlineData("xadd-cross-page")]
    [InlineData("cmpxchg8b-mismatch")]
    public unsafe void MemoryRmwRereadsTheOperandAfterSuspendingOnWrite(string form)
    {
        using var warm = new CpuSandbox();
        using var cold = new CpuSandbox();
        var crossPage = form == "xadd-cross-page";
        var protectedPage = crossPage ? Data + Page : Data;
        var address = crossPage ? protectedPage - 2 : Data;
        var before = BitConverter.GetBytes(0x4433221189ABCDEFUL);
        var replacement = BitConverter.GetBytes(0x88776655FEDCBA98UL);
        byte[] code = form switch
        {
            "xchg" => [0x87, 0x07],                       // xchg [edi], eax (implicit lock)
            "add" => [0xF0, 0x01, 0x07],                  // lock add [edi], eax
            "add-imm" => [0xF0, 0x83, 0x07, 5],            // lock add dword [edi], 5
            "adc" => [0xF0, 0x11, 0x07],                  // lock adc [edi], eax
            "inc-byte" => [0xF0, 0xFE, 0x07],             // lock inc byte [edi]
            "neg" => [0xF0, 0xF7, 0x1F],                  // lock neg dword [edi]
            "shld" => [0x0F, 0xA4, 0x07, 4],             // shld [edi], eax, 4
            "bts" => [0xF0, 0x0F, 0xBA, 0x2F, 0],        // lock bts dword [edi], 0
            "xadd-cross-page" => [0xF0, 0x0F, 0xC1, 7],   // lock xadd [edi], eax
            "cmpxchg8b-mismatch" => [0xF0, 0x0F, 0xC7, 0x0F], // lock cmpxchg8b qword [edi]
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        var end = CpuSandbox.CodeAddress + (uint)code.Length;
        foreach (var cpu in new[] { warm, cold })
        {
            if (crossPage) cpu.MapMemory(protectedPage, Page);
            cpu[Reg.EDI] = address;
            cpu[Reg.EAX] = 0x1234;
            cpu[Reg.EDX] = 0x5678;
            cpu.Eflags = 0xA93;
            cpu.WriteCode(code);
        }
        warm.WriteMemory(address, replacement);
        warm.Run(end);
        Assert.Equal(EmuStatus.Stopped, warm.Status);

        cold.WriteMemory(address, before);
        cold.ProtectMemory(protectedPage, Page, 1);
        var initial = cold.Snapshot();
        var faults = new List<(uint Address, int Access)>();
        var root = GCHandle.Alloc(faults);
        try
        {
            X86Native.SetExtendedFaultCallback(cold.State, &YieldOnPageFault, GCHandle.ToIntPtr(root));
            cold.Run(end);
            Assert.Equal(EmuStatus.Yield, cold.Status);
            Assert.Equal(5, Assert.Single(faults).Access);
            Assert.Equal(protectedPage, faults[0].Address & ~(Page - 1));
            Assert.Equal(initial.Eip, cold.Eip);
            Assert.Equal(initial.Registers, cold.Snapshot().Registers);
            Assert.Equal(initial.Eflags, cold.Eflags);
            Assert.Equal(before, cold.ReadMemory(address, before.Length));

            cold.ProtectMemory(protectedPage, Page, 3);
            cold.WriteMemory(address, replacement); // Simulate another guest task modifying the operand.
            cold.Run(end);
            Assert.Equal(EmuStatus.Stopped, cold.Status);
            Assert.Equal(warm.Snapshot().Registers, cold.Snapshot().Registers);
            Assert.Equal(warm.Eflags, cold.Eflags);
            Assert.Equal(warm.ReadMemory(address, before.Length), cold.ReadMemory(address, before.Length));
            Assert.Single(faults);
        }
        finally
        {
            X86Native.SetFaultCallback(cold.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [Fact]
    public unsafe void NestedEnterPreservesAnOverlappingFrameChainWhenTheFinalStackPageYields()
    {
        using var cpu = new CpuSandbox();
        var sp = Data + 32;
        var frame = sp - 4;
        var finalSp = sp - 16 - 64;
        cpu[Reg.ESP] = sp;
        cpu[Reg.EBP] = sp - 8;
        cpu.WriteMemory(sp - 12, BitConverter.GetBytes(0x11223344u));
        cpu.WriteMemory(sp - 16, BitConverter.GetBytes(0x55667788u));
        cpu.WriteCode([0xC8, 64, 0, 3]); // enter 64, 3
        var end = CpuSandbox.CodeAddress + 4;
        var faults = new List<(uint Address, int Access)>();
        var root = GCHandle.Alloc(faults);
        try
        {
            X86Native.SetExtendedFaultCallback(cpu.State, &YieldOnPageFault, GCHandle.ToIntPtr(root));
            cpu.Run(end);
            Assert.Equal((finalSp, 5), Assert.Single(faults));
            Assert.Equal(EmuStatus.Yield, cpu.Status);
            Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
            Assert.Equal(sp, cpu[Reg.ESP]);
            Assert.Equal(sp - 8, cpu[Reg.EBP]);

            cpu.MapMemory(Data - Page, Page);
            cpu.Run(end);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(frame, cpu[Reg.EBP]);
            Assert.Equal(finalSp, cpu[Reg.ESP]);
            Assert.Equal(0x11223344u, BitConverter.ToUInt32(cpu.ReadMemory(sp - 8, 4)));
            Assert.Equal(0x55667788u, BitConverter.ToUInt32(cpu.ReadMemory(sp - 12, 4)));
            Assert.Equal(frame, BitConverter.ToUInt32(cpu.ReadMemory(sp - 16, 4)));
        }
        finally
        {
            X86Native.SetFaultCallback(cpu.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public unsafe void RepeatedComparisonPreservesInputFlagsAndCompletedIndicesAtAPageFault(bool scan, bool sliced)
    {
        using var cpu = new CpuSandbox();
        var pageBoundary = Data + Page;
        cpu.WriteMemory(pageBoundary - 1, [0x42]);
        cpu.WriteMemory(CpuSandbox.StackAddress, [0x42, 0x42]);
        cpu[Reg.EAX] = 0x42;
        cpu[Reg.ESI] = CpuSandbox.StackAddress;
        cpu[Reg.EDI] = pageBoundary - 1;
        cpu[Reg.ECX] = 2;
        cpu.Eflags = 0x203; // Compare success sets ZF and clears CF.
        var inputFlags = cpu.Eflags;
        cpu.WriteCode(scan ? [0xF3, 0xAE] : [0xF3, 0xA6]); // repe scasb / repe cmpsb
        var end = CpuSandbox.CodeAddress + 2;
        var faults = new List<(uint Address, int Access)>();
        var root = GCHandle.Alloc(faults);
        try
        {
            X86Native.SetExtendedFaultCallback(cpu.State, &YieldOnPageFault, GCHandle.ToIntPtr(root));
            if (sliced)
            {
                cpu.Run(end, maxInstructions: 1);
                Assert.Empty(faults);
                Assert.Equal(1u, cpu[Reg.ECX]);
            }
            cpu.Run(end);
            Assert.Equal((pageBoundary, 4), Assert.Single(faults));
            Assert.Equal(EmuStatus.Yield, cpu.Status);
            Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
            Assert.Equal(inputFlags, cpu.Eflags);
            Assert.Equal(1u, cpu[Reg.ECX]);
            Assert.Equal(pageBoundary, cpu[Reg.EDI]);
            Assert.Equal(CpuSandbox.StackAddress + (scan ? 0u : 1u), cpu[Reg.ESI]);

            cpu.MapMemory(pageBoundary, Page);
            cpu.WriteMemory(pageBoundary, [0x42]);
            cpu.Run(end);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(0u, cpu[Reg.ECX]);
            Assert.Equal(pageBoundary + 1, cpu[Reg.EDI]);
            Assert.Equal(0x40u, cpu.Eflags & 0x41u);
        }
        finally
        {
            X86Native.SetFaultCallback(cpu.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int YieldOnPageFault(IntPtr state, uint address, int access, IntPtr userdata)
    {
        ((List<(uint Address, int Access)>)GCHandle.FromIntPtr(userdata).Target!).Add((address, access));
        X86Native.EmuYield(state);
        return 1;
    }

    private static uint Prepare(CpuSandbox cpu, string instruction)
    {
        cpu[Reg.EAX] = instruction == "cmpxchg-write" ? (uint)BitConverter.SingleToInt32Bits(1.5f) : 0x0099AABB;
        cpu[Reg.EBX] = 0x00332211;
        cpu[Reg.ECX] = 0x00556677;
        cpu[Reg.EDX] = 0x00ABCDEF;
        cpu[Reg.EDI] = Data;
        cpu[Reg.EBP] = Data;
        if (instruction == "pusha-cross-page")
            cpu.MapMemory(Data + Page, Page);
        cpu[Reg.ESP] = instruction switch
        {
            "push" or "call" => Data + 4,
            "pusha" or "enter" => Data + 32,
            "pusha-cross-page" => Data + Page + 8,
            _ => Data
        };
        cpu.WriteXmm(0, [27, 13, 99, 0, 1, 2, 4, 8, 16, 32, 64, 128, 6, 50, 0, 71]);
        cpu.Eflags = 0x246;
        if (instruction == "execute")
        {
            cpu.Eip = Data;
            return Data + 5;
        }

        byte[] code = instruction switch
        {
            "push" => [0x50],                         // push eax
            "call" => [0xE8, 0, 0, 0, 0],             // call the next instruction
            "pusha" or "pusha-cross-page" => [0x60],
            "popa" => [0x61],
            "enter" => [0xC8, 0x08, 0, 0],            // enter 8, 0
            "leave" => [0xC9],
            "lock-inc" => [0xF0, 0xFF, 0x07],          // lock inc dword [edi]
            "xchg" => [0x87, 0x07],                   // xchg [edi], eax
            "cmpxchg-write" => [0xF0, 0x0F, 0xB1, 0x1F], // lock cmpxchg [edi], ebx
            "movdqa-store" => [0x66, 0x0F, 0x7F, 7],  // movdqa [edi], xmm0
            "movdqa-load" => [0x66, 0x0F, 0x6F, 7],   // movdqa xmm0, [edi]
            "fstp" => [0xD9, 0xE8, 0xD9, 0x1F],       // fld1; fstp dword [edi]
            "fistp" => [0xD9, 0xEB, 0xDF, 0x1F],      // fldpi; fistp word [edi] (inexact)
            "fstp80" => [0xD9, 0xE8, 0xDB, 0x3F],     // fld1; fstp tbyte [edi]
            "fnsave" => [0xD9, 0xE8, 0xDD, 0x37],     // fld1; fnsave [edi]
            "fxsave" => [0xD9, 0xE8, 0x0F, 0xAE, 7],  // fld1; fxsave [edi]
            "movntq" => [0xD9, 0xE8, 0x0F, 0xE7, 7],  // fld1; movntq [edi], mm0
            "fld" => [0xD9, 0x07],                    // fld dword [edi]
            _ => throw new ArgumentOutOfRangeException(nameof(instruction))
        };
        cpu.WriteCode(code);
        return CpuSandbox.CodeAddress + (uint)code.Length;
    }

    [UnmanagedCallersOnly]
    private static int PopulatePage(IntPtr state, uint address, int flags, IntPtr userdata)
    {
        var fault = (PageFault)GCHandle.FromIntPtr(userdata).Target!;
        try
        {
            fault.Accesses.Add((address, flags));
            if (fault.Accesses.Count != 1 || (address & ~(Page - 1)) != Data) return 0;
            if (fault.Suspend)
                X86Native.EmuYield(state);
            else
                fault.Install();
            return 1;
        }
        catch (Exception ex)
        {
            fault.Error = ex;
            return 0;
        }
    }

    private sealed class PageFault(CpuSandbox cpu, byte[] bytes, byte permissions, bool writeProtectionFault,
        bool suspend)
    {
        public bool Suspend { get; } = suspend;

        public void Install()
        {
            if (writeProtectionFault)
                cpu.ReplaceBackingPage(Data, bytes, permissions);
            else
            {
                cpu.MapMemory(Data, Page, permissions);
                cpu.WriteMemory(Data, bytes);
            }
        }

        public List<(uint Address, int Flags)> Accesses { get; } = [];
        public Exception? Error { get; set; }
    }
}
