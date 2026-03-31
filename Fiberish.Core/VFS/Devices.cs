using System.Security.Cryptography;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Native;

namespace Fiberish.VFS;

public class ConsoleInode : Inode, ITaskWaitSource, IDispatcherWaitSource
{
    private static long _nextPseudoConsoleIno = 0x100000;
    private readonly TtyDiscipline? _discipline;
    private readonly bool _isInput;

    public ConsoleInode(SuperBlock sb, bool isInput, TtyDiscipline? discipline = null)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 666
        _isInput = isInput;
        Ino = (ulong)Interlocked.Increment(ref _nextPseudoConsoleIno);
        _discipline = discipline;
    }

    /// <summary>
    ///     Indicates whether this inode is backed by a TTY discipline.
    /// </summary>
    public bool IsTty => _discipline != null;

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        return RegisterWaitCore(callback, events, dispatcher);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        return RegisterWaitHandleCore(callback, events, null, dispatcher);
    }

    public bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        if (_discipline == null)
            return false;

        if (_isInput)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
                return QueueReadinessRegistration.Register(callback, task, events,
                    new QueueReadinessWatch(POLLIN, () => _discipline.HasDataAvailable, _discipline.DataAvailable,
                        _discipline.DataAvailable.Reset));
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
                return _discipline.RegisterWriteWait(callback, task.CommonKernel);
        }

        return false;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        return RegisterWaitHandleCore(callback, events, task, null);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot create in /dev");
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot mkdir in /dev");
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot symlink in /dev");
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        throw new InvalidOperationException("Cannot link in /dev");
    }

    protected internal override int ReadSpan(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;

        if (_discipline != null) return _discipline.Read(task, buffer, linuxFile.Flags);

        return 0;
    }

    public override async ValueTask WaitForRead(LinuxFile linuxFile, FiberTask task)
    {
        if (!_isInput || _discipline == null) return;

        // Await the event. If already signaled, completes immediately.
        await _discipline.DataAvailable.WaitAsync(task);

        // Reset after waking up. This ensures:
        // 1. If event was already signaled, we wake immediately and retry Read()
        // 2. If queue is still empty after retry, next WaitForRead() will block
        _discipline.DataAvailable.Reset();
    }

    protected internal override int WriteSpan(FiberTask? task, LinuxFile linuxFile, ReadOnlySpan<byte> buffer,
        long offset)
    {
        if (_isInput) return 0;

        if (_discipline != null) return _discipline.Write(task, buffer);

        return buffer.Length;
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;

        if (_isInput)
        {
            if (_discipline == null)
                return (events & POLLIN) != 0 ? POLLIN : (short)0;

            var readWatch = new QueueReadinessWatch(POLLIN, () => _discipline.HasDataAvailable, _discipline.DataAvailable,
                _discipline.DataAvailable.Reset);
            return QueueReadinessRegistration.ComputeRevents(events, readWatch);
        }

        if (_discipline != null)
            return (events & POLLOUT) != 0 && _discipline.CanWriteOutput ? POLLOUT : (short)0;

        return (events & POLLOUT) != 0 ? POLLOUT : (short)0;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        return false;
    }

    private bool RegisterWaitCore(Action callback, short events, IReadyDispatcher? dispatcher)
    {
        if (_discipline == null)
            return false;

        if (_isInput)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
            {
                if (dispatcher?.Scheduler is not { } scheduler)
                    throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");

                var readWatch =
                    new QueueReadinessWatch(POLLIN, () => _discipline.HasDataAvailable, _discipline.DataAvailable,
                        _discipline.DataAvailable.Reset);
                return QueueReadinessRegistration.Register(callback, scheduler, events, readWatch);
            }
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
            {
                var scheduler = dispatcher?.Scheduler
                                ?? throw new InvalidOperationException(
                                    "TTY write wait requires an explicit scheduler.");
                return _discipline.RegisterWriteWait(callback, scheduler);
            }
        }

        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return null;
    }

    private IDisposable? RegisterWaitHandleCore(Action callback, short events, FiberTask? task,
        IReadyDispatcher? dispatcher)
    {
        if (_discipline == null)
            return null;

        if (_isInput)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
            {
                var readWatch =
                    new QueueReadinessWatch(POLLIN, () => _discipline.HasDataAvailable, _discipline.DataAvailable,
                        _discipline.DataAvailable.Reset);
                if (task != null)
                    return QueueReadinessRegistration.RegisterHandle(callback, task, events, readWatch);
                if (dispatcher?.Scheduler is { } scheduler)
                    return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch);
                throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");
            }
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
            {
                var scheduler = task?.CommonKernel
                                ?? dispatcher?.Scheduler
                                ?? throw new InvalidOperationException(
                                    "TTY write wait requires an explicit scheduler.");
                // Current TTY write wait registration has no cancellation API.
                // Fallback to bool-based registration with no-op handle.
                return _discipline.RegisterWriteWait(callback, scheduler)
                    ? NoopWaitRegistration.Instance
                    : null;
            }
        }

        return null;
    }

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
    {
        if (_discipline != null)
            return _discipline.Ioctl(task, request, arg);

        return -(int)Errno.ENOTTY;
    }

    public override int Truncate(long size)
    {
        return 0;
    }
}

public sealed class ControllingTtyInode : Inode, ITaskWaitSource, ITaskPollSource, IDispatcherWaitSource
{
    public ControllingTtyInode(SuperBlock sb)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 0666
        Ino = 2; // Dummy but distinct from ConsoleInode
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        if (ResolveDiscipline(null, linuxFile) is not { } tty)
            return false;

        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        if ((events & POLLIN) != 0)
        {
            var scheduler = dispatcher.Scheduler
                            ?? throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");
            var readWatch = new QueueReadinessWatch(POLLIN, () => tty.HasDataAvailable, tty.DataAvailable,
                tty.DataAvailable.Reset);
            return QueueReadinessRegistration.Register(callback, scheduler, events, readWatch);
        }

        if ((events & POLLOUT) != 0)
        {
            var scheduler = dispatcher.Scheduler
                            ?? throw new InvalidOperationException("TTY write wait requires an explicit scheduler.");
            return tty.RegisterWriteWait(callback, scheduler);
        }

        return false;
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        if (ResolveDiscipline(null, linuxFile) is not { } tty)
            return null;

        const short POLLIN = 0x0001;
        if ((events & POLLIN) != 0)
        {
            var scheduler = dispatcher.Scheduler
                            ?? throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");
            var readWatch = new QueueReadinessWatch(POLLIN, () => tty.HasDataAvailable, tty.DataAvailable,
                tty.DataAvailable.Reset);
            return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch);
        }

        return null;
    }

    short ITaskPollSource.Poll(LinuxFile linuxFile, FiberTask task, short events)
    {
        return PollCore(ResolveDiscipline(task, linuxFile), events);
    }

    public bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return false;

        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        if ((events & POLLIN) != 0)
        {
            var readWatch = new QueueReadinessWatch(POLLIN, () => tty.HasDataAvailable, tty.DataAvailable,
                tty.DataAvailable.Reset);
            return QueueReadinessRegistration.Register(callback, task, events, readWatch);
        }

        if ((events & POLLOUT) != 0)
            return tty.RegisterWriteWait(callback, task.CommonKernel);

        return false;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return null;

        const short POLLIN = 0x0001;
        if ((events & POLLIN) != 0)
        {
            var readWatch = new QueueReadinessWatch(POLLIN, () => tty.HasDataAvailable, tty.DataAvailable,
                tty.DataAvailable.Reset);
            return QueueReadinessRegistration.RegisterHandle(callback, task, events, readWatch);
        }

        return null;
    }

    protected internal override int ReadSpan(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return -(int)Errno.ENXIO;

        return tty.Read(task, buffer, linuxFile.Flags);
    }

    public override async ValueTask WaitForRead(LinuxFile linuxFile, FiberTask task)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return;

        await tty.DataAvailable.WaitAsync(task);
        tty.DataAvailable.Reset();
    }

    protected internal override int WriteSpan(FiberTask? task, LinuxFile linuxFile, ReadOnlySpan<byte> buffer,
        long offset)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return -(int)Errno.ENXIO;

        return tty.Write(task, buffer);
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        return PollCore(ResolveDiscipline(null, linuxFile), events);
    }

    private static short PollCore(TtyDiscipline? tty, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        const short POLLERR = 0x0008;
        const short POLLHUP = 0x0010;

        if (tty == null)
            return POLLHUP | POLLERR;

        var readWatch = new QueueReadinessWatch(POLLIN, () => tty.HasDataAvailable, tty.DataAvailable,
            tty.DataAvailable.Reset);
        var revents = QueueReadinessRegistration.ComputeRevents(events, readWatch);
        if ((events & POLLOUT) != 0 && tty.CanWriteOutput)
            revents |= POLLOUT;
        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return null;
    }

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
    {
        if (ResolveDiscipline(task, linuxFile) is not { } tty)
            return -(int)Errno.ENXIO;

        return tty.Ioctl(task, request, arg);
    }

    public override int Truncate(long size)
    {
        return 0;
    }

    private static TtyDiscipline? ResolveDiscipline(FiberTask? task, LinuxFile linuxFile)
    {
        if (linuxFile.PrivateData is TtyDiscipline cached)
            return cached;

        var tty = task?.Process.ControllingTty;
        if (tty != null)
            linuxFile.PrivateData = tty;
        return tty;
    }
}

public class RandomInode : Inode
{
    public RandomInode(SuperBlock sb)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 666
        Ino = 1; // Dummy
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot create in /dev/random");
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot mkdir in /dev/random");
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot symlink in /dev/random");
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        throw new InvalidOperationException("Cannot link in /dev/random");
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        RandomNumberGenerator.Fill(buffer);
        return buffer.Length;
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        return base.Poll(linuxFile, events);
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        // Writing to /dev/urandom updates the entropy pool. 
        // We can just ignore it or count it as success.
        return buffer.Length;
    }

    public override int Truncate(long size)
    {
        return 0;
    }
}

public class NullInode : Inode
{
    public NullInode(SuperBlock sb)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 666
        Ino = 1; // Dummy
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot create in /dev/null");
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot mkdir in /dev/null");
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        throw new InvalidOperationException("Cannot symlink in /dev/null");
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        throw new InvalidOperationException("Cannot link in /dev/null");
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        // /dev/null always returns EOF.
        return 0;
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        // /dev/null discards all bytes and reports success.
        return buffer.Length;
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        short revents = 0;
        if ((events & POLLIN) != 0) revents |= POLLIN;
        if ((events & POLLOUT) != 0) revents |= POLLOUT;
        return revents;
    }

    public override int Truncate(long size)
    {
        return 0;
    }
}
