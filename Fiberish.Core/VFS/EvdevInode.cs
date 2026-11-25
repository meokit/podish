using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>An evdev keyboard or absolute pointer with an independent queue per open.</summary>
internal sealed class EvdevInode : Inode, ITaskWaitSource, IPollNotificationSource
{
    private const int EventSize32 = 16, EventSize64 = 24, MaxQueuedEvents = 256;
    private const int QueueLimit = MaxQueuedEvents * EventSize64;
    private const ushort Synchronize = 0, Key = 1, Relative = 2, Absolute = 3;
    internal const int AbsoluteMaximum = 32767;
    private readonly KernelScheduler _scheduler;
    private readonly byte[] _name, _keys = new byte[96], _pressed = new byte[96];
    private readonly bool _pointer;
    private readonly Dictionary<LinuxFile, Client> _clients = [];
    private LinuxFile? _grab;
    private int _buttons;
    private (int X, int Y)? _absolutePosition;

    private sealed class Client(KernelScheduler scheduler)
    {
        internal readonly TtyInputQueue Queue = new(scheduler);
        internal int Clock;
        internal bool Revoked;
    }

    internal EvdevInode(SuperBlock sb, KernelScheduler scheduler, bool pointer)
    {
        SuperBlock = sb;
        _scheduler = scheduler;
        _pointer = pointer;
        Type = InodeType.CharDev;
        Mode = 0x1B6;
        Rdev = pointer ? 0x0D41u : 0x0D40u;
        Ino = 0x20000 + Rdev;
        _name = Encoding.ASCII.GetBytes(pointer ? "Podish Mouse\0" : "Podish Keyboard\0");
        if (pointer)
        {
            for (var button = 0x110; button <= 0x112; button++) SetBit(_keys, button, true);
        }
        else
        {
            for (var code = 1; code < 256; code++) SetBit(_keys, code, true);
        }
    }

    public override void Open(LinuxFile file)
    {
        base.Open(file);
        var client = new Client(_scheduler);
        _clients.Add(file, client);
        file.PrivateData = client;
    }

    public override void Release(LinuxFile file)
    {
        if (_grab == file) _grab = null;
        _clients.Remove(file);
        base.Release(file);
    }

    private static Client State(LinuxFile file) => (Client)file.PrivateData!;

    internal void PublishKey(uint code, bool pressed)
    {
        if (code is 0 or >= 256 || HasBit(_pressed, (int)code) == pressed) return;
        SetBit(_pressed, (int)code, pressed);
        Publish([(Key, (ushort)code, pressed ? 1 : 0), (Synchronize, 0, 0)]);
    }

    internal void PublishPointer((int X, int Y)? position, int buttons)
    {
        Span<(ushort Type, ushort Code, int Value)> events = stackalloc (ushort, ushort, int)[4];
        var count = 0;
        if (position is { } point && point != _absolutePosition)
        {
            if (point.X != _absolutePosition?.X) events[count++] = (Absolute, 0, point.X);
            if (point.Y != _absolutePosition?.Y) events[count++] = (Absolute, 1, point.Y);
            _absolutePosition = point;
            events[count++] = (Synchronize, 0, 0);
            // Deliver the position before a click received at that position.
            Publish(events[..count]);
            count = 0;
        }
        for (var i = 0; i < 3; i++)
        {
            var button = 1 << i;
            if (((buttons ^ _buttons) & button) == 0) continue;
            var down = (buttons & button) != 0;
            SetBit(_pressed, 0x110 + i, down);
            events[count++] = (Key, (ushort)(0x110 + i), down ? 1 : 0);
        }
        _buttons = buttons;
        if (count == 0) return;
        events[count++] = (Synchronize, 0, 0);
        Publish(events[..count]);
    }

    internal void PublishWheel(int x, int y)
    {
        Span<(ushort Type, ushort Code, int Value)> events = stackalloc (ushort, ushort, int)[3];
        var count = 0;
        if (x != 0) events[count++] = (Relative, 6, x); // REL_HWHEEL
        if (y != 0) events[count++] = (Relative, 8, y); // REL_WHEEL
        if (count == 0) return;
        events[count++] = (Synchronize, 0, 0);
        Publish(events[..count]);
    }

    private void Publish(ReadOnlySpan<(ushort Type, ushort Code, int Value)> events)
    {
        foreach (var (file, client) in _clients)
        {
            if (client.Revoked || (_grab != null && _grab != file)) continue;
            var queue = client.Queue;
            var time = client.Clock == 0
                ? (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10
                : Stopwatch.GetTimestamp() * (Int128)1_000_000 / Stopwatch.Frequency;
            var seconds = (long)(time / 1_000_000);
            var micros = (long)(time % 1_000_000);
            var overflow = queue.Count + events.Length * EventSize64 > QueueLimit;
            var packet = new byte[(events.Length + (overflow ? 1 : 0)) * EventSize64];
            var offset = 0;
            if (overflow)
            {
                queue.Clear();
                WriteEvent(packet, seconds, micros, Synchronize, 3, 0); // SYN_DROPPED
                offset = EventSize64;
            }
            foreach (var (type, code, value) in events)
            {
                WriteEvent(packet.AsSpan(offset, EventSize64), seconds, micros, type, code, value);
                offset += EventSize64;
            }
            queue.Write(packet);
            file.NotifyAsyncReadable(_scheduler);
        }
    }

    private static void WriteEvent(Span<byte> record, long seconds, long micros, ushort type, ushort code, int value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(record, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(record[8..], micros);
        BinaryPrimitives.WriteUInt16LittleEndian(record[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(record[18..], code);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], value);
    }

    private static bool HasBit(byte[] bits, int n) => (bits[n / 8] & (1 << (n % 8))) != 0;

    private static void SetBit(byte[] bits, int n, bool set)
    {
        var mask = (byte)(1 << (n % 8));
        if (set) bits[n / 8] |= mask;
        else bits[n / 8] &= (byte)~mask;
    }

    public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint address)
    {
        if ((request >> 8 & 0xff) != 0x45) return -(int)Errno.ENOTTY;
        var size = (int)(request >> 16 & 0x3fff);
        var client = State(file);
        if (client.Revoked) return -(int)Errno.ENODEV;
        if (request == 0x40044591) // EVIOCREVOKE
        {
            if (address != 0) return -(int)Errno.EINVAL;
            if (_grab == file) _grab = null;
            client.Revoked = true;
            client.Queue.Clear();
            client.Queue.DataAvailable.Set();
            return 0;
        }
        if (request == 0x40044590) // EVIOCGRAB
        {
            if (address == 0)
            {
                if (_grab != file) return -(int)Errno.EINVAL;
                _grab = null;
            }
            else
            {
                if (_grab != null) return -(int)Errno.EBUSY;
                _grab = file;
            }
            return 0;
        }
        if (request == 0x400445A0) // EVIOCSCLOCKID
        {
            Span<byte> value = stackalloc byte[4];
            if (!task.CPU.CopyFromUser(address, value)) return -(int)Errno.EFAULT;
            var clock = BinaryPrimitives.ReadInt32LittleEndian(value);
            if (clock is not (0 or 1 or 7)) return -(int)Errno.EINVAL;
            if (clock != client.Clock && client.Queue.Count != 0)
                client.Queue.Clear();
            client.Clock = clock;
            return 0;
        }
        if (request >> 30 != 2) return -(int)Errno.ENOTTY;

        var number = request & 0xff;
        if (_pointer && number is 0x40 or 0x41) // EVIOCGABS(ABS_X/ABS_Y)
        {
            Span<byte> info = stackalloc byte[24];
            info.Clear();
            var value = number == 0x40 ? _absolutePosition?.X : _absolutePosition?.Y;
            BinaryPrimitives.WriteInt32LittleEndian(info, value ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(info[8..], AbsoluteMaximum);
            return task.CPU.CopyToUser(address, info[..Math.Min(size, info.Length)]) ? 0 : -(int)Errno.EFAULT;
        }
        ReadOnlySpan<byte> data = number switch
        {
            0x01 => [1, 0, 1, 0], // EVIOCGVERSION 1.0.1
            0x02 => [6, 0, 0, 0, 0, 0, 1, 0], // BUS_VIRTUAL, version 1
            0x06 => _name,
            0x07 => "podish/input\0"u8,
            0x09 => [0, 0, 0, 0], // EVIOCGPROP
            0x18 => _pressed,
            0x19 or 0x1A or 0x1B => [0, 0, 0, 0], // LED, sound, switch state
            0x20 => _pointer ? [15, 0, 0, 0] : [3, 0, 0, 0],
            0x21 => _keys,
            0x22 when _pointer => [0x40, 1], // REL_HWHEEL, REL_WHEEL
            0x23 when _pointer => [3, 0, 0, 0], // ABS_X, ABS_Y
            >= 0x22 and <= 0x3f => [0, 0, 0, 0],
            _ => []
        };
        if (data.IsEmpty) return number == 8 ? -(int)Errno.ENOENT : -(int)Errno.ENOTTY;
        if (task.CPU.IsAArch64 && data.Length < 8 && (number is 0x09 or >= 0x18 and <= 0x3f))
        {
            // EVIOCGBIT and the state queries return an array of unsigned long.
            Span<byte> bits = stackalloc byte[8];
            bits.Clear();
            data.CopyTo(bits);
            var bitLength = Math.Min(size, bits.Length);
            return task.CPU.CopyToUser(address, bits[..bitLength]) ? bitLength : -(int)Errno.EFAULT;
        }

        var length = Math.Min(size, data.Length);
        return task.CPU.CopyToUser(address, data[..length]) ? (number <= 2 ? 0 : length) : -(int)Errno.EFAULT;
    }

    public override int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset)
    {
        var client = State(file);
        if (client.Revoked) return -(int)Errno.ENODEV;
        if (buffer.IsEmpty) return 0;
        var isAArch64 = task?.CPU.IsAArch64 == true;
        var eventSize = isAArch64 ? EventSize64 : EventSize32;
        var count = Math.Min(MaxQueuedEvents, buffer.Length / eventSize);
        if (count == 0) return -(int)Errno.EINVAL;
        if (isAArch64) return client.Queue.Read(buffer[..(count * EventSize64)], file.Flags);

        // Keep the queue independent of the reader's architecture. An open file
        // descriptor can survive an exec into a different ABI.
        Span<byte> events = stackalloc byte[count * EventSize64];
        var read = client.Queue.Read(events, file.Flags);
        if (read <= 0) return read;
        var records = read / EventSize64;
        for (var i = 0; i < records; i++)
        {
            var source = events.Slice(i * EventSize64, EventSize64);
            var target = buffer.Slice(i * EventSize32, EventSize32);
            source[..4].CopyTo(target);
            source.Slice(8, 4).CopyTo(target[4..]);
            source[16..].CopyTo(target[8..]);
        }
        return records * EventSize32;
    }

    public override async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        if (!engine.IsAArch64) return await ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);

        // A 24-byte input_event can straddle pages. Copy each event through
        // the user-memory API so page boundaries do not truncate records.
        var client = State(file);
        var nonBlocking = task == null || (file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 8) != 0;
        var record = new byte[EventSize64];
        var total = 0;
        for (var i = 0; i < iovs.Count; i++)
        {
            var iov = iovs[i];
            if (iov.Len == 0) continue;
            if (iov.Len < EventSize64) return total > 0 ? total : -(int)Errno.EINVAL;

            var cursor = 0u;
            while (cursor <= iov.Len - EventSize64)
            {
                if (client.Revoked) return total > 0 ? total : -(int)Errno.ENODEV;

                var read = client.Queue.Read(record, file.Flags);
                if (read == -(int)Errno.EAGAIN && !nonBlocking && total == 0)
                {
                    if (await WaitForRead(file, task!) == AwaitResult.Interrupted)
                        return -(int)Errno.ERESTARTSYS;
                    continue;
                }
                if (read <= 0) return total > 0 ? total : read;

                var rc = await engine.CopyUserIoAsync(iov.BaseAddr + cursor, record, write: true);
                if (rc < 0) return rc;
                total += read;
                cursor += EventSize64;
            }
        }

        return total;
    }

    public override short Poll(LinuxFile file, short events)
    {
        var client = State(file);
        if (client.Revoked) return (short)(LinuxConstants.EPOLLHUP | LinuxConstants.EPOLLERR);
        return (short)(events & (client.Queue.Count > 0 ? LinuxConstants.POLLIN : 0));
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task) =>
        await State(file).Queue.DataAvailable.WaitInterruptiblyAsync(task);

    bool ITaskWaitSource.RegisterWait(LinuxFile file, FiberTask task, Action callback, short events) =>
        ((ITaskWaitSource)this).RegisterWaitHandle(file, task, callback, events) != null;

    IDisposable? ITaskWaitSource.RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        var client = State(file);
        var queue = client.Queue;
        return QueueReadinessRegistration.RegisterHandle(callback, task, events,
            new QueueReadinessWatch(LinuxConstants.POLLIN, () => client.Revoked || queue.Count > 0,
                queue.DataAvailable, queue.DataAvailable.Reset));
    }

    IDisposable IPollNotificationSource.SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher,
        Func<short, bool> notify, short interests, bool exclusive) =>
        State(file).Queue.DataAvailable.SubscribePoll(dispatcher, notify, interests, exclusive);
}
