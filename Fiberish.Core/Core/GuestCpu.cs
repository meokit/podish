using Fiberish.Cpu.Native;
using Fiberish.X86.Native;

namespace Fiberish.Core;

public enum GuestArchitecture { I386, AArch64 }

public interface IGuestCpu
{
    GuestArchitecture Architecture { get; }
    ulong InstructionPointer { get; set; }
    ulong StackPointer { get; set; }
    uint ReadSyscall(Span<ulong> arguments);
    void SetSyscallResult(long value);
    void Run(ulong endPc = 0, ulong maxInsts = 0);
    int Step();
}

public partial class Engine
{
#if FIBER_GUEST_I386 && FIBER_GUEST_AARCH64
    public GuestArchitecture Architecture { get; private set; } = GuestArchitecture.I386;
    public bool IsAArch64 => Architecture == GuestArchitecture.AArch64;
#elif FIBER_GUEST_AARCH64
    public GuestArchitecture Architecture => GuestArchitecture.AArch64;
    public bool IsAArch64 => true;
#elif FIBER_GUEST_I386
    public GuestArchitecture Architecture => GuestArchitecture.I386;
    public bool IsAArch64 => false;
#else
#error Enable at least one of FiberGuestI386 and FiberGuestAArch64.
#endif

    public void SelectArchitecture(GuestArchitecture architecture)
    {
        switch (architecture)
        {
#if FIBER_GUEST_I386
            case GuestArchitecture.I386:
                break;
#endif
#if FIBER_GUEST_AARCH64
            case GuestArchitecture.AArch64:
                break;
#endif
            default:
                throw new NotSupportedException($"Guest architecture {architecture} is disabled.");
        }
#if FIBER_GUEST_I386 && FIBER_GUEST_AARCH64
        Architecture = architecture;
#endif
        if (IsAArch64) RegisterArmFaultCallback();
    }

    internal void ResetForExec(GuestArchitecture architecture, ulong instructionPointer, ulong stackPointer)
    {
        SelectArchitecture(architecture);
        if (IsAArch64)
            AArch64Native.Reset(State);
        else
        {
            for (var i = 0; i < 8; i++) RegWrite((Reg)i, 0);
            SetSegBase(Seg.FS, 0);
            SetSegBase(Seg.GS, 0);
            Eflags = 0x202;
        }
        InstructionPointer = instructionPointer;
        StackPointer = stackPointer;
    }

    // Native slots 0..30 hold X0..X30. Slot 31 denotes SP, including when copying a signal frame.
    internal ulong ReadArmRegister(int register) => AArch64Native.Read(State, register);
    internal void WriteArmRegister(int register, ulong value) => AArch64Native.Write(State, register, value);

    internal ulong ReturnValue => IsAArch64 ? ReadArmRegister(0) : RegRead(Reg.EAX);

    public ulong InstructionPointer
    {
        get => IsAArch64 ? ReadArmRegister(32) : Eip;
        set { if (IsAArch64) WriteArmRegister(32, value); else Eip = checked((uint)value); }
    }

    public ulong StackPointer
    {
        get => IsAArch64 ? ReadArmRegister(31) : RegRead(Reg.ESP);
        set { if (IsAArch64) WriteArmRegister(31, value); else RegWrite(Reg.ESP, checked((uint)value)); }
    }

    public unsafe uint ReadSyscall(Span<ulong> arguments)
    {
        if (arguments.Length < 6) throw new ArgumentException("Six syscall arguments are required.", nameof(arguments));
        if (IsAArch64)
        {
            fixed (ulong* values = arguments) return AArch64Native.ReadSyscall(State, values);
        }
        arguments[0] = RegRead(Reg.EBX);
        arguments[1] = RegRead(Reg.ECX);
        arguments[2] = RegRead(Reg.EDX);
        arguments[3] = RegRead(Reg.ESI);
        arguments[4] = RegRead(Reg.EDI);
        arguments[5] = RegRead(Reg.EBP);
        return RegRead(Reg.EAX);
    }

    public void SetSyscallResult(long value)
    {
        if (IsAArch64) WriteArmRegister(0, unchecked((ulong)value));
        else RegWrite(Reg.EAX, unchecked((uint)value));
    }
}
