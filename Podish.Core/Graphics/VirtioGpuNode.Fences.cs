using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Podish.Core.Graphics;

internal sealed partial class VirtioGpuNode
{
    internal sealed class Context(VenusRenderer renderer, uint id, uint ringCount, uint capset)
    {
        private readonly Lock _gate = new();
        private readonly ulong[] _nextFence = new ulong[ringCount];
        private readonly LinkedList<(ulong Sequence, Fence Fence)>?[] _pending =
            new LinkedList<(ulong, Fence)>[ringCount];
        private int _refs = 1;

        internal VenusRenderer Renderer { get; } = renderer;
        internal uint Id { get; } = id;
        internal uint RingCount { get; } = ringCount;
        internal bool IsVirgl { get; } = capset != VenusRenderer.CapsetId;

        internal void Retain() => Interlocked.Increment(ref _refs);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            Renderer.DestroyContext(this);
            for (uint ring = 0; ring < RingCount; ring++) Retire(ring, ulong.MaxValue);
        }

        internal Fence? CreateFence(uint ring, KernelScheduler scheduler)
        {
            LinkedListNode<(ulong Sequence, Fence Fence)> submission;
            lock (_gate)
            {
                var sequence = ++_nextFence[ring];
                var pending = _pending[ring] ??= new LinkedList<(ulong, Fence)>();
                submission = pending.AddLast((sequence, new Fence(scheduler, Id, ring, sequence)));
            }

            if (Renderer.SubmitFence(this, ring, submission.Value.Sequence)) return submission.Value.Fence;
            // A failed submission must not retire preceding fences on this ring.
            lock (_gate) submission.List?.Remove(submission);
            return null;
        }

        internal void Retire(uint ring, ulong sequence)
        {
            if (ring >= RingCount) return;
            lock (_gate)
            {
                var pending = _pending[ring];
                while (pending?.First is { } next && next.Value.Sequence <= sequence)
                {
                    pending.RemoveFirst();
                    next.Value.Fence.Signal();
                }
            }
        }
    }

    internal sealed class Fence(KernelScheduler scheduler, uint context = 0, uint ring = 0,
        ulong sequence = 0, bool signaled = false)
    {
        private readonly object _gate = new();
        private readonly uint _context = context;
        private readonly uint _ring = ring;
        private readonly ulong _sequence = sequence;
        private volatile bool _signaled = signaled;
        internal AsyncWaitQueue Ready { get; } = new(scheduler);
        internal bool Signaled => _signaled;

        internal bool TryMerge(Fence other, out Fence merged)
        {
            merged = _sequence > other._sequence ? this : other;
            return _context != 0 && _context == other._context && _ring == other._ring;
        }

        internal void Signal()
        {
            lock (_gate)
            {
                if (_signaled) return;
                _signaled = true;
                Monitor.PulseAll(_gate);
            }
            if (scheduler.IsSchedulerThread) Ready.Set();
            else scheduler.ScheduleFromAnyThread(Ready.Set);
        }

        internal int Wait()
        {
            // The native sync thread retires the fence without needing the emulated scheduler.
            lock (_gate)
            {
                var deadline = Environment.TickCount64 + 10000;
                while (!_signaled)
                {
                    var timeLeft = deadline - Environment.TickCount64;
                    if (timeLeft <= 0) return -(int)Errno.ETIMEDOUT;
                    Monitor.Wait(_gate, (int)timeLeft);
                }
            }
            return 0;
        }
    }

    private static int Execute(OpenFile open, FiberTask task, uint arg, Span<byte> data, int size)
    {
        const uint fenceIn = 1, fenceOut = 2, ringIndex = 4;
        var flags = U32(data, 0);
        if (open.Context is not { } context || (flags & ~(fenceIn | fenceOut | ringIndex)) != 0 ||
            U32(data, 40) != 0 || U32(data, 44) != 0)
            return Invalid;

        var ring = (flags & ringIndex) != 0 ? U32(data, 32) : 0;
        if (ring >= context.RingCount) return Invalid;
        if ((flags & fenceIn) != 0)
        {
            var input = task.Process.Syscalls.GetFD((int)U32(data, 28), task);
            if (input == null) return -(int)Errno.EBADF;
            if (input.OpenedInode is not FenceInode fenceFile) return Invalid;
            var wait = fenceFile.Fence.Wait();
            if (wait != 0) return wait;
        }

        var handles = U32(data, 24);
        if (handles > 4096) return Invalid;
        Span<byte> entries = handles <= 64 ? stackalloc byte[(int)handles * 4] : new byte[handles * 4];
        if (handles != 0 && !Read(task.CPU, U64(data, 16), entries)) return Fault;
        for (var offset = 0; offset < entries.Length; offset += 4)
            if (!open.Handles.ContainsKey(U32(entries, offset))) return Missing;

        var rc = Submit(context, task.CPU, U64(data, 8), U32(data, 4));
        if (rc != 0 || ((flags & fenceOut) == 0 && handles == 0)) return rc;

        var fence = context.CreateFence(ring, task.CommonKernel);
        if (fence == null) return Invalid;
        for (var offset = 0; offset < entries.Length; offset += 4)
            open.Handles[U32(entries, offset)].Resource.LastFence = fence;

        if ((flags & fenceOut) == 0) return 0;
        return WriteFd(task, arg, data[..size], 28, CreateFenceFd(task, fence));
    }

    private static int CreateFenceFd(FiberTask task, Fence? fence)
    {
        var syscalls = task.Process.Syscalls;
        var inode = new FenceInode(syscalls.MemfdSuperBlock, fence ?? new Fence(task.CommonKernel, signaled: true));
        var file = MakeFile(syscalls, inode, "anon_inode:[sync_file]", FileFlags.O_RDONLY);
        return syscalls.AllocFD(file, closeOnExec: true, task: task);
    }

    private static int WriteFd(FiberTask task, uint arg, Span<byte> data, int offset, int fd)
    {
        if (fd < 0) return fd;
        Put(data, offset, (uint)fd);
        if (Write(task.CPU, arg, data)) return 0;
        task.Process.Syscalls.TryFreeFD(fd, task);
        return Fault;
    }

    internal static Fence? GetFence(LinuxFile? file) => (file?.OpenedInode as FenceInode)?.Fence;

    private sealed class FenceInode : Inode, ITaskWaitSource, IPollNotificationSource
    {
        internal FenceInode(SuperBlock superBlock, Fence fence)
        {
            Fence = fence;
            SuperBlock = superBlock;
            Type = InodeType.File;
            Mode = 0x100;
            AnonymousInodeLifecycle.Initialize(this, "Venus sync file");
        }

        internal Fence Fence { get; }

        public override void Release(LinuxFile file)
        {
            base.Release(file);
            AnonymousInodeLifecycle.CloseAliasesAndFinalize(this, "Venus sync file close");
        }

        public override short Poll(LinuxFile file, short events) => Fence.Signaled ? (short)(events & PollEvents.POLLIN) : (short)0;

        public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events) =>
            RegisterWaitHandle(file, task, callback, events) != null;

        public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events) =>
            QueueReadinessRegistration.RegisterHandle(callback, task, events,
                new QueueReadinessWatch(PollEvents.POLLIN, () => Fence.Signaled, Fence.Ready));

        IDisposable IPollNotificationSource.SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher,
            Func<short, bool> notify, short interests, bool exclusive) =>
            Fence.Ready.SubscribePoll(dispatcher, notify, interests, exclusive);

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
        {
            const uint syncFileInfo = 0xc0383e04;
            if (request != syncFileInfo) return -(int)Errno.ENOTTY;
            Span<byte> data = stackalloc byte[56];
            if (!Read(task.CPU, arg, data)) return Fault;
            if (U32(data, 36) != 0 || U32(data, 44) != 0) return Invalid;

            var status = Fence.Signaled ? 1u : 0u;
            if (U32(data, 40) != 0)
            {
                Span<byte> detail = stackalloc byte[80];
                detail.Clear();
                "controlq"u8.CopyTo(detail);
                "virtio_gpu"u8.CopyTo(detail[32..]);
                Put(detail, 64, status);
                if (!Write(task.CPU, U64(data, 48), detail)) return Fault;
            }

            data[..32].Clear();
            "virtio_gpu-controlq"u8.CopyTo(data);
            Put(data, 32, status);
            Put(data, 40, 1);
            return Write(task.CPU, arg, data) ? 0 : Fault;
        }
    }
}
