using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Tests.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class EvdevTests
{
    [Fact]
    public void EvdevSeatWithoutFramebuffer_DeliversHostKeyAndPointer()
    {
        using var env = new SyscallTestEnv();
        env.Sm.MountStandardDev();
        var input = new VirtualConsoleInput();
        input.RegisterEvdev(env.Sm, env.Scheduler);
        using var keyboard = new LinuxFile(env.Sm.PathWalk("/dev/input/event0"),
            FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        using var pointer = new LinuxFile(env.Sm.PathWalk("/dev/input/event1"),
            FileFlags.O_RDONLY | FileFlags.O_NONBLOCK);
        env.Scheduler.RegisterProcess(env.Process);
        env.Task.Continuation = () =>
        {
            input.PublishKey(30, true);
            input.PublishPointer(0, 0, 0, (100, 75, 201, 151));

            Span<byte> events = stackalloc byte[3 * 16];
            Assert.Equal(2 * 16, keyboard.IoInode!.ReadToHost(env.Task, keyboard, events, 0));
            AssertEvent(events, 0, 1, 30, 1);
            Assert.Equal(3 * 16, pointer.IoInode!.ReadToHost(env.Task, pointer, events, 0));
            AssertEvent(events, 0, 3, 0, EvdevInode.AbsoluteMaximum / 2);
            AssertEvent(events, 1, 3, 1, EvdevInode.AbsoluteMaximum / 2);

            input.Detach();
            input.PublishKey(30, false);
            Assert.Equal(0, keyboard.IoInode.Poll(keyboard, LinuxConstants.POLLIN));
            env.Task.Exited = true;
            env.Task.Status = FiberTaskStatus.Terminated;
        };
        env.Scheduler.RegisterTask(env.Task);
        env.Scheduler.Run(100);
        Assert.True(env.Task.Exited);
    }

    [Fact]
    public void AbsoluteMouse_ReportsPositionBeforeButtonAndKeepsWheelEvents()
    {
        using var env = new SyscallTestEnv();
        var mouse = new EvdevInode(env.Sb, env.Scheduler, pointer: true);
        using var file = new LinuxFile(new Dentry("event1", mouse, env.Sb.Root, env.Sb),
            FileFlags.O_RDONLY | FileFlags.O_NONBLOCK, env.Sm.AnonMount);
        const uint address = 0x10000;
        Span<byte> info = stackalloc byte[24];

        Assert.Equal(2, mouse.Ioctl(file, env.Task, 0x80024522, address)); // EVIOCGBIT(EV_REL)
        Assert.True(env.Engine.CopyFromUser(address, info[..2]));
        Assert.Equal(0x40, info[0]); // REL_HWHEEL and no REL_X or REL_Y
        Assert.Equal(1, info[1]); // REL_WHEEL

        mouse.PublishPointer((120, 200), buttons: 1);
        mouse.PublishWheel(0, -1);
        mouse.PublishPointer((120, 200), buttons: 0);
        Span<byte> events = stackalloc byte[9 * 16];
        Assert.Equal(events.Length, mouse.ReadToHost(env.Task, file, events, 0));
        AssertEvent(events, 0, 3, 0, 120);
        AssertEvent(events, 1, 3, 1, 200);
        AssertEvent(events, 2, 0, 0, 0);
        AssertEvent(events, 3, 1, 0x110, 1);
        AssertEvent(events, 4, 0, 0, 0);
        AssertEvent(events, 5, 2, 8, -1);
        AssertEvent(events, 6, 0, 0, 0);
        AssertEvent(events, 7, 1, 0x110, 0);
        AssertEvent(events, 8, 0, 0, 0);

        Assert.Equal(0, mouse.Ioctl(file, env.Task, 0x80184541, address)); // EVIOCGABS(ABS_Y)
        Assert.True(env.Engine.CopyFromUser(address, info));
        Assert.Equal(200, BinaryPrimitives.ReadInt32LittleEndian(info));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(info[4..]));
        Assert.Equal(EvdevInode.AbsoluteMaximum, BinaryPrimitives.ReadInt32LittleEndian(info[8..]));
    }

    private static void AssertEvent(ReadOnlySpan<byte> events, int index, ushort type, ushort code, int value)
    {
        var record = events.Slice(index * 16, 16);
        Assert.Equal(type, BinaryPrimitives.ReadUInt16LittleEndian(record[8..]));
        Assert.Equal(code, BinaryPrimitives.ReadUInt16LittleEndian(record[10..]));
        Assert.Equal(value, BinaryPrimitives.ReadInt32LittleEndian(record[12..]));
    }
}
