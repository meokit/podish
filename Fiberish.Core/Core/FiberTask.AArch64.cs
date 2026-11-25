using System.Buffers.Binary;
using Fiberish.Native;

namespace Fiberish.Core;

public partial class FiberTask
{
    // Linux arm64 UAPI: rt_sigframe starts with siginfo, then ucontext (mcontext aligned to 16).
    private const int ArmUContext = 128;
    private const int ArmMContext = ArmUContext + 176;
    private const int ArmReserved = ArmMContext + 288;
    private const int ArmFrameSize = ArmReserved + 4096;
    private const uint FpsimdMagic = 0x46508001;
    private const int FpsimdSize = 528;

    internal static void WriteAArch64SigInfo(Span<byte> bytes, SigInfo info)
    {
        bytes[..128].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, info.Signo);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], info.Errno);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], info.Code);
        var payload = bytes[16..];
        if (info.HasAddr)
            BinaryPrimitives.WriteUInt64LittleEndian(payload, info.ArmFaultAddress ?? info.Addr);
        else if (info.HasFdPayload)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload, info.Band);
            BinaryPrimitives.WriteInt32LittleEndian(payload[8..], info.Fd);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload, info.Code == -2 ? info.TimerId : info.Pid);
            BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], info.Code == -2 ? (uint)info.Overrun : info.Uid);
            if (info.Signo == (int)Signal.SIGCHLD && info.Code > 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload[8..], info.Status);
                BinaryPrimitives.WriteInt64LittleEndian(payload[16..], info.Utime);
                BinaryPrimitives.WriteInt64LittleEndian(payload[24..], info.Stime);
            }
            else BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], info.Value);
        }
    }

    private bool DeliverAArch64Signal(int sig, SigAction action, ulong mask, SigInfo info)
    {
        var sp = CPU.StackPointer;
        var top = sp;
        if (AltStackSize != 0 && (action.Flags & LinuxConstants.SA_ONSTACK) != 0 &&
            (sp > uint.MaxValue || !IsOnAltStack((uint)sp)))
            top = (ulong)AltStackSp + AltStackSize;
        if (top > 1UL << 32 || top < ArmFrameSize + 16) return false;
        var frame = (uint)((top - ArmFrameSize - 16) & ~15UL);
        var data = new byte[ArmFrameSize + 16];
        WriteAArch64SigInfo(data, info);
        void Put64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
        void Put32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Put64(ArmUContext + 16, AltStackSp);
        Put32(ArmUContext + 24, (uint)GetAltStackFlags(sp <= uint.MaxValue ? (uint)sp : 0));
        Put64(ArmUContext + 32, AltStackSize);
        Put64(ArmUContext + 40, mask);
        Put64(ArmMContext, info.HasAddr ? info.ArmFaultAddress ?? info.Addr : 0);
        for (int i = 0; i <= 32; i++) Put64(ArmMContext + 8 + 8 * i, CPU.ReadArmRegister(i));
        Put64(ArmMContext + 272, CPU.ReadArmRegister(34));
        Put32(ArmReserved, FpsimdMagic);
        Put32(ArmReserved + 4, FpsimdSize);
        Put32(ArmReserved + 8, (uint)CPU.ReadArmRegister(36));
        Put32(ArmReserved + 12, (uint)CPU.ReadArmRegister(35));
        for (int i = 0; i < 64; i++) Put64(ArmReserved + 16 + 8 * i, CPU.ReadArmRegister(64 + i));
        Put64(ArmFrameSize, CPU.ReadArmRegister(29));
        Put64(ArmFrameSize + 8, CPU.ReadArmRegister(30));
        if (!CPU.CopyToUser(frame, data)) return false;
        if (((uint)AltStackFlags & LinuxConstants.SS_AUTODISARM) != 0) ResetAltStack();
        CPU.WriteArmRegister(0, (uint)sig);
        if ((action.Flags & LinuxConstants.SA_SIGINFO) != 0)
        {
            CPU.WriteArmRegister(1, frame);
            CPU.WriteArmRegister(2, frame + ArmUContext);
        }
        CPU.WriteArmRegister(29, frame + ArmFrameSize);
        CPU.WriteArmRegister(30, (action.Flags & LinuxConstants.SA_RESTORER) != 0
            ? action.Restorer : Process.Syscalls.RtSigReturnAddr);
        CPU.StackPointer = frame;
        CPU.InstructionPointer = action.Handler;
        return true;
    }

    internal bool RestoreAArch64SignalFrame()
    {
        var sp = CPU.StackPointer;
        var data = new byte[ArmFrameSize];
        if ((sp & 15) != 0 || sp > uint.MaxValue || !CPU.CopyFromUser((uint)sp, data)) return false;
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
        if (U32(ArmReserved) != FpsimdMagic || U32(ArmReserved + 4) != FpsimdSize ||
            U64(ArmReserved + FpsimdSize) != 0 || (U64(ArmMContext + 272) & 0xf) != 0) return false;
        for (int i = 0; i <= 32; i++) CPU.WriteArmRegister(i, U64(ArmMContext + 8 + 8 * i));
        CPU.WriteArmRegister(34, U64(ArmMContext + 272));
        CPU.WriteArmRegister(35, U32(ArmReserved + 12));
        CPU.WriteArmRegister(36, U32(ArmReserved + 8));
        for (int i = 0; i < 64; i++) CPU.WriteArmRegister(64 + i, U64(ArmReserved + 16 + 8 * i));
        if (U64(ArmUContext + 16) <= uint.MaxValue && U64(ArmUContext + 32) <= uint.MaxValue)
            SetAltStack((uint)U64(ArmUContext + 16), (uint)U64(ArmUContext + 32), U32(ArmUContext + 24));
        SignalMask = U64(ArmUContext + 40) & ~((1UL << 8) | (1UL << 18));
        RestoreDeferredSignalMaskIfAny();
        return true;
    }

    private void ExitArmRobustList(uint head)
    {
        Span<byte> header = stackalloc byte[24];
        if (!CPU.CopyFromUser(head, header)) return;
        var offset = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        var pending = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]) & ~1UL;
        var entry = BinaryPrimitives.ReadUInt64LittleEndian(header) & ~1UL;
        void Notify(ulong node, bool pendingOp)
        {
            var address = unchecked(node + (ulong)offset);
            if (node <= uint.MaxValue && address <= uint.MaxValue && (address & 3) == 0)
                HandleFutexDeath(Process.Syscalls, (uint)address, pendingOp);
        }
        var next = header[..8];
        for (int i = 0; i < 2048 && entry != 0 && entry != head && entry <= uint.MaxValue; i++)
        {
            if (!CPU.CopyFromUser((uint)entry, next)) break;
            if (entry != pending) Notify(entry, false);
            entry = BinaryPrimitives.ReadUInt64LittleEndian(next) & ~1UL;
        }
        if (pending != 0) Notify(pending, true);
    }
}
