using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>Three-byte PS/2 mousedev protocol. Each open file has an independent input stream.</summary>
internal sealed class Ps2MouseInode : Inode, ITaskWaitSource, IPollNotificationSource
{
    private readonly KernelScheduler _scheduler;
    private readonly HashSet<LinuxFile> _clients = [];
    private int _buttons;

    internal Ps2MouseInode(SuperBlock sb, KernelScheduler scheduler)
    {
        SuperBlock = sb;
        _scheduler = scheduler;
        Type = InodeType.CharDev;
        Mode = 0x1b6;
        Rdev = 0x0d3f; // input major 13, mixed mouse minor 63
        Ino = 0x20d3f;
    }

    public override void Open(LinuxFile file)
    {
        base.Open(file);
        var queue = new TtyInputQueue(_scheduler);
        _clients.Add(file);
        file.PrivateData = queue;
    }

    public override void Release(LinuxFile file)
    {
        _clients.Remove(file);
        base.Release(file);
    }

    internal void Move(int dx, int dy, int buttons)
    {
        if (dx == 0 && dy == 0 && buttons == _buttons) return;
        _buttons = buttons & 7;
        // A PS/2 packet reports signed 9-bit motion, positive Y pointing up.
        do
        {
            var x = Math.Clamp(dx, -255, 255);
            var y = -Math.Clamp(dy, -255, 255);
            byte[] packet = [(byte)(8 | _buttons | (x < 0 ? 16 : 0) | (y < 0 ? 32 : 0)), (byte)x, (byte)y];
            foreach (var file in _clients)
            {
                var queue = Queue(file);
                if (queue.Count >= 4096) continue;
                queue.Write(packet);
                file.NotifyAsyncReadable(_scheduler);
            }
            dx -= x;
            dy += y;
        } while (dx != 0 || dy != 0);
    }

    private static TtyInputQueue Queue(LinuxFile file) => (TtyInputQueue)file.PrivateData!;

    public override int WriteFromHost(FiberTask? task, LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        var queue = Queue(file);
        var written = 0;
        foreach (var command in buffer)
        {
            // As a standard PS/2 mouse, identify as ID 0 even after IntelliMouse negotiation.
            if (queue.Count >= 4096) break;
            switch (command)
            {
                case 0xf2: queue.Write([0xfa, 0]); break;
                case 0xff: queue.Write([0xfa, 0xaa, 0]); break;
                case 0xe9: queue.Write([0xfa, (byte)_buttons, 2, 100]); break;
                case 0xeb: queue.Write([0xfa, (byte)(8 | _buttons), 0, 0]); break;
                default: queue.Write(0xfa); break;
            }
            written++;
        }
        if (written > 0) file.NotifyAsyncReadable(_scheduler);
        return written;
    }

    public override int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset) =>
        buffer.IsEmpty ? 0 : Queue(file).Read(buffer, file.Flags);

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags) => ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags) => WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);

    public override short Poll(LinuxFile file, short events) =>
        (short)(events & (Queue(file).Count > 0 ? 5 : 4));

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task) =>
        await Queue(file).DataAvailable.WaitInterruptiblyAsync(task);

    public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events) =>
        RegisterWaitHandle(file, task, callback, events) != null;

    public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        var queue = Queue(file);
        return QueueReadinessRegistration.RegisterHandle(callback, task, events,
            new QueueReadinessWatch(1, () => queue.Count > 0, queue.DataAvailable, queue.DataAvailable.Reset));
    }

    IDisposable IPollNotificationSource.SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher,
        Func<short, bool> notify, short interests, bool exclusive) =>
        Queue(file).DataAvailable.SubscribePoll(dispatcher, notify, interests, exclusive);
}
