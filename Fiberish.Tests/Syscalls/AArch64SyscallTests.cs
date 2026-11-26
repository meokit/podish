#if FIBER_GUEST_AARCH64
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class AArch64SyscallTests
{
    [Fact]
    public void SvcUsesX8AndReturnsFullWidthErrorsInX0()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var engine = env.Engine;
        const uint code = 0x14000;
        Assert.True(engine.CopyToUser(code, [0x01, 0x00, 0x00, 0xd4]));
        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(env.Mem, engine, code,
            LinuxConstants.PageSize, Protection.Read | Protection.Exec));

        engine.InstructionPointer = code;
        engine.WriteArmRegister(8, 172); // getpid
        engine.WriteArmRegister(0, ulong.MaxValue);
        engine.Step();

        Assert.Equal((ulong)env.Process.TGID, engine.ReadArmRegister(0));
        Assert.Equal(172UL, engine.ReadArmRegister(8));
        Assert.Equal(code + 4UL, engine.InstructionPointer);

        engine.InstructionPointer = code;
        engine.WriteArmRegister(8, 888); // Private debug syscall exists only on i386.
        engine.Step();
        Assert.Equal(-(long)Errno.ENOSYS, (long)engine.ReadArmRegister(0));
    }

    [Fact]
    public void OpenFlagsAndStatFollowTheNativeAbi()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.PutString(0x10000, "/abi");
        var fd = OpenFile(env);
        Assert.Equal(0, Invoke(env, 46, fd, 1234)); // ftruncate
        Assert.Equal(0, Invoke(env, 80, fd, 0x11000)); // fstat

        var stat = Read(env, 0x11000, 128);
        Assert.Equal(0x8000u, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(16)) & 0xf000u);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(20)));
        Assert.Equal(1234L, BinaryPrimitives.ReadInt64LittleEndian(stat.AsSpan(48)));
        Assert.Equal((uint)LinuxConstants.PageSize, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(56)));

        Assert.Equal(-(long)Errno.ENOTDIR, Invoke(env, 56, AtFdcwd, 0x10000, 1 << 14));
        env.PutString(0x10000, "/");
        var dirfd = Invoke(env, 56, AtFdcwd, 0x10000, 1 << 14);
        Assert.True(dirfd >= 0);
        Assert.Equal(1 << 14, Invoke(env, 25, (ulong)dirfd, 3) & (1 << 14)); // F_GETFL
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileIoPreservesA64BitOffsetAndReads16ByteIovecs(bool high)
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.PutString(0x10000, "/abi");
        var fd = OpenFile(env);
        const ulong offset = 0x1_1234_5678;
        Assert.Equal((long)offset, Invoke(env, 62, fd, offset, 0)); // lseek
        Assert.Equal((long)offset + 2, Invoke(env, 62, fd, 2, 1));

        var bias = high ? 0x40_0000_0000UL : 0;
        var first = bias + 0x11000;
        var second = bias + 0x12000;
        var vectors = bias + 0x13000;
        if (high)
        {
            const ulong size = 3 * LinuxConstants.PageSize;
            Assert.Equal(first, ProcessAddressSpaceSync.Mmap(env.Mem, env.Engine, first, size,
                Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous,
                null, 0, "[aarch64 iovec test]"));
            Assert.True(env.Mem.PrefaultRange(first, size, env.Engine, true));
        }
        Assert.True(env.Engine.CopyToUser(first, "ab"u8));
        Assert.True(env.Engine.CopyToUser(second, "cd"u8));
        Span<byte> iov = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(iov, first);
        BinaryPrimitives.WriteUInt64LittleEndian(iov[8..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(iov[16..], second);
        BinaryPrimitives.WriteUInt64LittleEndian(iov[24..], 2);
        Assert.True(env.Engine.CopyToUser(vectors, iov));
        Assert.Equal(4, Invoke(env, 70, fd, vectors, 2, 0)); // pwritev
        Assert.Equal(4, Invoke(env, 67, fd, first, 4, 0)); // pread64
        Assert.Equal("abcd"u8.ToArray(), Read(env, first, 4));

        Assert.Equal(0, Invoke(env, 62, fd, 0, 0)); // lseek
        Assert.Equal(2, Invoke(env, 64, fd, second, 2)); // write
        Assert.Equal(0, Invoke(env, 62, fd, 0, 0));
        Assert.Equal(2, Invoke(env, 63, fd, first, 2)); // read
        Assert.Equal("cd"u8.ToArray(), Read(env, first, 2));

        Assert.Equal(4, Invoke(env, 69, fd, vectors, 2, 0)); // preadv
        Assert.Equal("cd"u8.ToArray(), Read(env, first, 2));
        Assert.Equal("cd"u8.ToArray(), Read(env, second, 2));

        BinaryPrimitives.WriteUInt64LittleEndian(iov, LinuxConstants.TaskSizeAArch64 - 1);
        Assert.True(env.Engine.CopyToUser(vectors, iov));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 70, fd, vectors, 2, 0));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 64, fd, LinuxConstants.TaskSizeAArch64, 1)); // write
    }

    [Fact]
    public void MmapUsesByteOffsetsAndZeroExtendsAddresses()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.PutString(0x10000, "/abi");
        var fd = OpenFile(env);
        Assert.True(env.Engine.CopyToUser(0x11000, "page"u8));
        Assert.Equal(4, Invoke(env, 68, fd, 0x11000, 4, LinuxConstants.PageSize)); // pwrite64

        const uint mapping = 0x9000_0000;
        const uint flags = (uint)(MapFlags.Private | MapFlags.Fixed);
        Assert.Equal(-(long)Errno.EINVAL,
            Invoke(env, 222, mapping, LinuxConstants.PageSize, (uint)Protection.Read, flags, fd, 1));
        Assert.Equal((long)mapping,
            Invoke(env, 222, mapping, LinuxConstants.PageSize, (uint)Protection.Read, flags, fd,
                LinuxConstants.PageSize));
        Assert.Equal("page"u8.ToArray(), Read(env, mapping, 4));
        Assert.Equal(0, Invoke(env, 215, mapping, LinuxConstants.PageSize)); // munmap
    }

    [Fact]
    public void ClockAndSignalStateUseNativeWordSizes()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        Assert.True(env.Engine.CopyToUser(0x11000, Enumerable.Repeat((byte)0xff, 17).ToArray()));
        Assert.Equal(0, Invoke(env, 113, 0, 0x11000)); // clock_gettime
        var time = Read(env, 0x11000, 17);
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(time) > 0);
        Assert.InRange(BinaryPrimitives.ReadInt64LittleEndian(time.AsSpan(8)), 0, 999_999_999);
        Assert.Equal(0xff, time[16]);

        var action = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(action, 0x14000);
        BinaryPrimitives.WriteUInt64LittleEndian(action.AsSpan(8), LinuxConstants.SA_RESTART);
        BinaryPrimitives.WriteUInt64LittleEndian(action.AsSpan(16), 0xfedc_ba98_7654_3210);
        BinaryPrimitives.WriteUInt64LittleEndian(action.AsSpan(24), 1UL << 32);
        Assert.True(env.Engine.CopyToUser(0x12000, action));
        Assert.Equal(0, Invoke(env, 134, (int)Signal.SIGUSR1, 0x12000, 0, 8)); // rt_sigaction
        Assert.Equal(0, Invoke(env, 134, (int)Signal.SIGUSR1, 0, 0x13000, 8));
        Assert.Equal(action, Read(env, 0x13000, 32));

        Assert.Equal(0, Invoke(env, 99, 0x12000, 24)); // set_robust_list
        Assert.Equal(0, Invoke(env, 100, 0, 0x13000, 0x13008)); // get_robust_list
        var robust = Read(env, 0x13000, 16);
        Assert.Equal(0x12000UL, BinaryPrimitives.ReadUInt64LittleEndian(robust));
        Assert.Equal(24UL, BinaryPrimitives.ReadUInt64LittleEndian(robust.AsSpan(8)));

        Assert.Equal(0, Invoke(env, 100, 0, 0x13000, 0x13000));
        Assert.Equal(0x12000UL, BinaryPrimitives.ReadUInt64LittleEndian(Read(env, 0x13000, 8)));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 100, 0, 0x13000, 0));
    }

    [Fact]
    public void ExecReadsEightByteArgumentPointers()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.PutString(0x10000, "/missing");
        env.PutString(0x11000, "one");
        env.PutString(0x11010, "two");
        var vector = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(vector, 0x11000);
        BinaryPrimitives.WriteUInt64LittleEndian(vector.AsSpan(8), 0x11010);
        Assert.True(env.Engine.CopyToUser(0x12000, vector));
        var (args, error) = env.Sm.ReadUserCStringVectorBytes(0x12000);
        Assert.Equal(0, error);
        Assert.Equal(new[] { "one", "two" }, args.Select(arg => System.Text.Encoding.UTF8.GetString(arg)));
        Assert.Equal(-(long)Errno.ENOENT, Invoke(env, 221, 0x10000, 0x12000, 0)); // execve

        BinaryPrimitives.WriteUInt64LittleEndian(vector.AsSpan(8), 0x1_0001_1010);
        Assert.True(env.Engine.CopyToUser(0x12000, vector));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 221, 0x10000, 0x12000, 0));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SignalHandlerAndTrampolineRestoreFullWidthIntegerAndVectorState(bool useRestorer, bool high)
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var engine = env.Engine;
        var bias = high ? 0x40_0000_0000UL : 0;
        var main = bias + 0x14000;
        var handler = main + 4;
        var restorer = main + 6 * 4;
        var result = bias + 0x10000;
        var stackTop = bias + 0x13ff0;
        var actionPtr = bias + 0x11000;
        var oldActionPtr = actionPtr + 64;
        var altStack = result + 5 * LinuxConstants.PageSize;
        const ulong altStackSize = 2 * LinuxConstants.PageSize;
        const int signal = (int)Signal.SIGUSR1;
        const ulong originalX0 = 0xfedc_ba98_7654_3210;
        const ulong originalX8 = 0x1100_2200_3300_4400;
        const ulong originalV0Low = 0xf0f1_f2f3_f4f5_f6f7;
        const ulong originalV0High = 0x1111_2222_3333_4444;
        uint[] instructions =
        [
            0xd503201f, // nop
            0xb9000260, // str w0, [x19]
            0xd2800000, // mov x0, #0
            0xd2800028, // mov x8, #1
            0x4f00e400, // movi v0.16b, #0
            0xd65f03c0, // ret
            0xd2801168, // mov x8, #139
            0xd4000001  // svc #0
        ];
        if (high)
        {
            const ulong regionSize = 7 * LinuxConstants.PageSize;
            Assert.Equal(result, ProcessAddressSpaceSync.Mmap(env.Mem, engine, result, regionSize,
                Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous,
                null, 0, "[aarch64 signal test]"));
            Assert.True(env.Mem.PrefaultRange(result, regionSize, engine, true));
            Span<byte> stack = stackalloc byte[24];
            stack.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(stack, altStack);
            BinaryPrimitives.WriteUInt64LittleEndian(stack[16..], altStackSize);
            Assert.True(engine.CopyToUser(actionPtr, stack));
            Assert.Equal(0, Invoke(env, 132, actionPtr, 0)); // sigaltstack
            Assert.Equal(0, Invoke(env, 132, 0, oldActionPtr));
            Assert.Equal(stack.ToArray(), Read(env, oldActionPtr, stack.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(stack, LinuxConstants.TaskSizeAArch64 - LinuxConstants.PageSize);
            Assert.True(engine.CopyToUser(actionPtr, stack));
            Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 132, actionPtr, 0));
        }
        Assert.True(engine.CopyToUser(main, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instructions.AsSpan())));
        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(env.Mem, engine, main,
            LinuxConstants.PageSize, Protection.Read | Protection.Exec));
        Span<byte> action = stackalloc byte[32];
        action.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(action, handler);
        BinaryPrimitives.WriteUInt64LittleEndian(action[8..],
            LinuxConstants.SA_SIGINFO | (useRestorer ? LinuxConstants.SA_RESTORER : 0) |
            (high ? LinuxConstants.SA_ONSTACK : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(action[16..], useRestorer ? restorer : ulong.MaxValue);
        Assert.True(engine.CopyToUser(actionPtr, action));
        Assert.Equal(0, Invoke(env, 134, signal, actionPtr, 0, 8)); // rt_sigaction
        Assert.Equal(0, Invoke(env, 134, signal, 0, oldActionPtr, 8));
        Assert.Equal(action.ToArray(), Read(env, oldActionPtr, action.Length));

        engine.InstructionPointer = main;
        engine.StackPointer = stackTop;
        engine.WriteArmRegister(0, originalX0);
        engine.WriteArmRegister(8, originalX8);
        engine.WriteArmRegister(19, result);
        engine.WriteArmRegister(64, originalV0Low);
        engine.WriteArmRegister(65, originalV0High);
        env.Task.PostSignal(signal);
        env.Task.Status = FiberTaskStatus.Running;
        env.Task.RunSlice(1);

        Assert.Equal(handler + 4UL, engine.InstructionPointer);
        Assert.Equal(signal, BinaryPrimitives.ReadInt32LittleEndian(Read(env, result, 4)));
        Assert.NotEqual(stackTop, engine.StackPointer);
        Assert.Equal(0UL, engine.StackPointer & 15);
        if (high)
        {
            Assert.InRange(engine.StackPointer, altStack + 16, altStack + altStackSize);
            Assert.Equal(-(int)Errno.EPERM, env.Task.SetAltStack(0, 0, LinuxConstants.SS_DISABLE));
        }
        for (var i = 0; i < 3; i++) engine.Step();
        Assert.Equal(0UL, engine.ReadArmRegister(0));
        Assert.Equal(1UL, engine.ReadArmRegister(8));
        Assert.Equal(0UL, engine.ReadArmRegister(64) | engine.ReadArmRegister(65));

        engine.Step(); // Return from the handler to the selected trampoline.
        Assert.Equal(useRestorer ? restorer : env.Sm.RtSigReturnAddr, engine.InstructionPointer);
        engine.Step(); // mov x8, #139
        Assert.Equal(139UL, engine.ReadArmRegister(8));
        engine.Step(); // svc #0
        Assert.Equal(main, engine.InstructionPointer);
        Assert.Equal(stackTop, engine.StackPointer);
        Assert.Equal(originalX0, engine.ReadArmRegister(0));
        Assert.Equal(originalX8, engine.ReadArmRegister(8));
        Assert.Equal(originalV0Low, engine.ReadArmRegister(64));
        Assert.Equal(originalV0High, engine.ReadArmRegister(65));
    }

    [Fact]
    public void CloneAndExitUseHighStackAndTidAddresses()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighThreadPage(env);
        var stack = page + LinuxConstants.PageSize - 16;
        var parentTidPtr = page + 4;
        var childTidPtr = page + 8;
        var tls = page + 16;
        var flags = LinuxConstants.CLONE_VM | LinuxConstants.CLONE_SIGHAND | LinuxConstants.CLONE_THREAD |
                    LinuxConstants.CLONE_SETTLS | LinuxConstants.CLONE_PARENT_SETTID |
                    LinuxConstants.CLONE_CHILD_SETTID | LinuxConstants.CLONE_CHILD_CLEARTID;
        var childTid = (int)Invoke(env, 220, flags, stack, parentTidPtr, tls, childTidPtr); // clone
        Assert.True(childTid > 0);
        var child = Assert.IsType<FiberTask>(env.Scheduler.GetTask(childTid));
        Assert.Equal(stack, child.CPU.StackPointer);
        Assert.Equal(tls, child.CPU.ReadArmRegister(33));
        Assert.Equal(childTid, BinaryPrimitives.ReadInt32LittleEndian(Read(env, parentTidPtr, 4)));
        Assert.Equal(childTid, BinaryPrimitives.ReadInt32LittleEndian(Read(env, childTidPtr, 4)));
        Assert.Equal(childTidPtr, child.ChildClearTidPtr);
        var waiter = env.Sm.Futex.PrepareWait(FutexKey.Private(env.Mem, page, 8));
        child.ClearChildTidAndWake();
        Assert.True(waiter.Result);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(Read(env, childTidPtr, 4)));

        Assert.Equal(env.Task.TID, Invoke(env, 96, parentTidPtr)); // set_tid_address
        Assert.Equal(parentTidPtr, env.Task.ChildClearTidPtr);
        env.Task.ClearChildTidAndWake();
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(Read(env, parentTidPtr, 4)));
    }

    [Fact]
    public void RobustListExitAndFutexWakeUseDistinctHighAndLowAddresses()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighThreadPage(env);
        var head = page + 32;
        var entry = page + 64;
        var futex = entry + 8;
        var lenPtr = page + 96;
        var headPtr = page + 104;
        var lowFutex = (uint)futex;
        Span<byte> robust = stackalloc byte[24];
        robust.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(robust, entry);
        BinaryPrimitives.WriteInt64LittleEndian(robust[8..], 8);
        Assert.True(env.Engine.CopyToUser(head, robust));
        BinaryPrimitives.WriteUInt64LittleEndian(robust, head);
        BinaryPrimitives.WriteUInt32LittleEndian(robust[8..], (uint)env.Task.TID | LinuxConstants.FUTEX_WAITERS);
        Assert.True(env.Engine.CopyToUser(entry, robust[..12]));
        Assert.Equal(0, Invoke(env, 99, head, 24)); // set_robust_list
        Assert.Equal(0, Invoke(env, 100, 0, headPtr, lenPtr)); // get_robust_list
        Assert.Equal(head, BinaryPrimitives.ReadUInt64LittleEndian(Read(env, headPtr, 8)));
        Assert.Equal(24UL, BinaryPrimitives.ReadUInt64LittleEndian(Read(env, lenPtr, 8)));

        const uint wakePrivate = 1 | LinuxConstants.FUTEX_PRIVATE_FLAG;
        var key = FutexKey.Private(env.Mem, page, (ushort)(futex - page));
        var waiter = env.Sm.Futex.PrepareWait(key);
        Assert.Equal(0, Invoke(env, 98, lowFutex, wakePrivate, 1));
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, Invoke(env, 98, futex, wakePrivate, 1));
        Assert.True(waiter.Result);

        const uint requeuePrivate = LinuxConstants.FUTEX_REQUEUE | LinuxConstants.FUTEX_PRIVATE_FLAG;
        var target = page + 128;
        waiter = env.Sm.Futex.PrepareWait(key);
        Assert.Equal(1, Invoke(env, 98, futex, requeuePrivate, 0, 1, target));
        Assert.Equal(0, Invoke(env, 98, (uint)target, wakePrivate, 1));
        Assert.Equal(1, Invoke(env, 98, target, wakePrivate, 1));
        Assert.True(waiter.Result);

        var timeout = page + 160;
        Span<byte> timespec = stackalloc byte[16];
        timespec.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(timespec[8..], 1);
        Assert.True(env.Engine.CopyToUser(timeout, timespec));
        Assert.Equal(-(long)Errno.EAGAIN, Invoke(env, 98, futex, LinuxConstants.FUTEX_PRIVATE_FLAG, 0, timeout));

        waiter = env.Sm.Futex.PrepareWait(key);
        env.Task.ExitRobustList();
        Assert.True(waiter.Result);
        Assert.Equal(LinuxConstants.FUTEX_WAITERS | LinuxConstants.FUTEX_OWNER_DIED,
            BinaryPrimitives.ReadUInt32LittleEndian(Read(env, futex, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Read(env, lowFutex, 4)));
    }

    private static ulong MapHighThreadPage(SyscallTestEnv env)
    {
        const ulong page = 0x40_0001_0000;
        Assert.Equal(page, ProcessAddressSpaceSync.Mmap(env.Mem, env.Engine, page, LinuxConstants.PageSize,
            Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous,
            null, 0, "[aarch64 thread test]"));
        Assert.True(env.Mem.PrefaultRange(page, LinuxConstants.PageSize, env.Engine, true));
        return page;
    }

    [Fact]
    public void FramebufferIoctlsUseLinuxArm64ScreenInfoAndPaletteLayouts()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.Sm.Framebuffer = new FramebufferDevice(64, 32, 32);
        env.Sm.MountStandardDev();
        env.PutString(0x10000, "/dev/fb0");
        var fd = Invoke(env, 56, AtFdcwd, 0x10000, 2);
        Assert.True(fd >= 0);
        Assert.Equal(0, Invoke(env, 29, (ulong)fd, 0x4602, 0x11000)); // FBIOGET_FSCREENINFO
        var data = Read(env, 0x11000, 80);
        Assert.Equal(16384, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24)));
        Assert.Equal(256, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)));

        Span<byte> palette = stackalloc byte[40];
        palette.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(palette[4..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(palette[8..], 0x13000);
        BinaryPrimitives.WriteUInt64LittleEndian(palette[16..], 0x13002);
        BinaryPrimitives.WriteUInt64LittleEndian(palette[24..], 0x13004);
        Assert.True(env.Engine.CopyToUser(0x12000, palette));
        byte[] colors = [1, 2, 3, 4, 5, 6];
        Assert.True(env.Engine.CopyToUser(0x13000, colors));
        Assert.Equal(0, Invoke(env, 29, (ulong)fd, 0x4605, 0x12000)); // FBIOPUTCMAP
        Assert.True(env.Engine.CopyToUser(0x13000, new byte[6]));
        Assert.Equal(0, Invoke(env, 29, (ulong)fd, 0x4604, 0x12000)); // FBIOGETCMAP
        Assert.Equal(colors, Read(env, 0x13000, 6));

        BinaryPrimitives.WriteUInt64LittleEndian(palette[24..], 0x1_0001_3004);
        Assert.True(env.Engine.CopyToUser(0x12000, palette));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 29, (ulong)fd, 0x4605, 0x12000));
    }

    [Fact]
    public void EvdevReadReturnsNativeInputEventRecords()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var keyboard = new EvdevInode(env.Sb, env.Scheduler, pointer: false);
        var dentry = new Dentry("event0", keyboard, env.Sb.Root, env.Sb);
        var fd = env.Sm.AllocFD(new LinuxFile(dentry, FileFlags.O_RDONLY | FileFlags.O_NONBLOCK, env.Sm.AnonMount));
        keyboard.PublishKey(30, true);

        const uint eventBuffer = 0x10ff8; // The first record crosses a page boundary.
        Assert.Equal(48, Invoke(env, 63, (ulong)fd, eventBuffer, 48)); // read
        var events = Read(env, eventBuffer, 48);
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(events) > 0);
        Assert.InRange(BinaryPrimitives.ReadInt64LittleEndian(events.AsSpan(8)), 0, 999_999);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(16)));
        Assert.Equal(30, BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(18)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(events.AsSpan(20)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(40)));
        Assert.Equal(8, Invoke(env, 29, (ulong)fd, 0x80084520, 0x13000)); // EVIOCGBIT(0, 8)
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(Read(env, 0x13000, 8)));
    }

    [Fact]
    public void DrmAcceptsNativeVersionAndPlaneResourceRequests()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var card = new DrmCardInode(env.Sb, env.Scheduler, gpuBuffers: false);
        var dentry = new Dentry("card0", card, env.Sb.Root, env.Sb);
        var fd = (ulong)env.Sm.AllocFD(new LinuxFile(dentry, FileFlags.O_RDWR, env.Sm.AnonMount));
        var version = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(version.AsSpan(16), 6);
        BinaryPrimitives.WriteUInt64LittleEndian(version.AsSpan(24), 0x12000);
        Assert.True(env.Engine.CopyToUser(0x11000, version));
        Assert.Equal(0, Invoke(env, 29, fd, 0xc0406400, 0x11000)); // DRM_IOCTL_VERSION
        Assert.Equal("podish"u8.ToArray(), Read(env, 0x12000, 6));
        version = Read(env, 0x11000, 64);
        Assert.Equal(6UL, BinaryPrimitives.ReadUInt64LittleEndian(version.AsSpan(16)));
        Assert.Equal(8UL, BinaryPrimitives.ReadUInt64LittleEndian(version.AsSpan(32)));
        Assert.Equal(-(long)Errno.ENOTTY, Invoke(env, 29, fd, 0xc0246400, 0x11000));

        Assert.Equal(0, Invoke(env, 29, fd, 0xc01064b5, 0x13000)); // DRM_IOCTL_MODE_GETPLANERESOURCES
        var resources = Read(env, 0x13000, 16);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(resources.AsSpan(8)));
    }

    [Fact]
    public void ProcCpuInfoDescribesTheReadingGuest()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        env.Sm.MountStandardProc();
        env.PutString(0x10000, "/proc/cpuinfo");
        var fd = Invoke(env, 56, AtFdcwd, 0x10000, 0);
        Assert.True(fd >= 0);
        var bytes = Invoke(env, 63, (ulong)fd, 0x11000, 256);
        Assert.InRange(bytes, 1, 256);
        var cpuInfo = System.Text.Encoding.UTF8.GetString(Read(env, 0x11000, (int)bytes));
        Assert.Contains("AArch64", cpuInfo);
        Assert.DoesNotContain("GenuineIntel", cpuInfo);
    }

    private const ulong AtFdcwd = unchecked((ulong)-100L);

    private static ulong OpenFile(SyscallTestEnv env)
    {
        var fd = Invoke(env, 56, AtFdcwd, 0x10000, 0x42, 0x1a4); // openat, O_CREAT | O_RDWR, 0644
        Assert.True(fd >= 0, $"openat returned {fd}");
        return (ulong)fd;
    }

    private static byte[] Read(SyscallTestEnv env, ulong address, int count)
    {
        var result = new byte[count];
        Assert.True(env.Engine.CopyFromUser(address, result));
        return result;
    }

    private static long Invoke(SyscallTestEnv env, uint number, params ulong[] arguments)
    {
        env.Engine.WriteArmRegister(8, number);
        for (var i = 0; i < 6; i++)
            env.Engine.WriteArmRegister(i, i < arguments.Length ? arguments[i] : 0);
        Assert.True(env.Sm.Handle(env.Engine, 0x80));
        Assert.Null(env.Task.PendingSyscall);
        return (long)env.Engine.ReadArmRegister(0);
    }
}
#endif
