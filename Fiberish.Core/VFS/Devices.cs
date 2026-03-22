using System.Security.Cryptography;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Native;

namespace Fiberish.VFS;

public class ConsoleInode : Inode, ITaskWaitSource, IDispatcherWaitSource
{
    private static readonly Stream _stdout = Console.OpenStandardOutput();
    private static readonly Stream _stdin = Console.OpenStandardInput();
    private readonly TtyDiscipline? _discipline;
    private readonly bool _isInput;

    public ConsoleInode(SuperBlock sb, bool isInput, TtyDiscipline? discipline = null)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 666
        _isInput = isInput;
        Ino = 1; // Dummy
        _discipline = discipline;
    }

    /// <summary>
    ///     Indicates whether this inode is backed by a TTY discipline.
    /// </summary>
    public bool IsTty => _discipline != null;

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

    public override int Read(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;

        if (_discipline != null) return _discipline.Read(task, buffer, linuxFile.Flags);

        return _stdin.Read(buffer);
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

    public override int Write(FiberTask? task, LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_isInput) return 0;

        if (_discipline != null) return _discipline.Write(task, buffer);

        _stdout.Write(buffer);
        _stdout.Flush();
        return buffer.Length;
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;

        short revents = 0;

        if (_isInput)
        {
            // Check if there's data available in the TTY discipline
            if ((events & POLLIN) != 0)
            {
                if (_discipline != null)
                {
                    // Process raw device input before readiness check so control chars
                    // (e.g. VINTR/^C) can generate signals immediately while callers
                    // are blocked in select/pselect/poll, without waiting for a read().
                    _discipline.ProcessPendingInput();

                    // Check if there's data in the input queue or pending input from device
                    if (_discipline.HasDataAvailable) revents |= POLLIN;
                }
                else
                {
                    // Direct stdin - always readable (simplified)
                    revents |= POLLIN;
                }
            }
        }
        else
        {
            if ((events & POLLOUT) != 0)
            {
                if (_discipline != null)
                {
                    if (_discipline.CanWriteOutput) revents |= POLLOUT;
                }
                else
                {
                    // Output - stdout is always writable
                    revents |= POLLOUT;
                }
            }
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        return false;
    }

    public bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        if (_discipline == null)
            return false;

        if (_isInput)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
            {
                _discipline.DataAvailable.Register(callback, task);
                return true;
            }
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
                return _discipline.RegisterWriteWait(callback, task.CommonKernel);
        }

        return false;
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        return RegisterWaitCore(callback, events, dispatcher);
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
                if (dispatcher?.Scheduler is { } scheduler)
                    _discipline.DataAvailable.Register(callback, scheduler);
                else
                    throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");
                return true;
            }
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
            {
                var scheduler = dispatcher?.Scheduler
                                ?? throw new InvalidOperationException("TTY write wait requires an explicit scheduler.");
                return _discipline.RegisterWriteWait(callback, scheduler);
            }
        }

        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return null;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        return RegisterWaitHandleCore(callback, events, task, null);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        return RegisterWaitHandleCore(callback, events, null, dispatcher);
    }

    private IDisposable? RegisterWaitHandleCore(Action callback, short events, FiberTask? task, IReadyDispatcher? dispatcher)
    {
        if (_discipline == null)
            return null;

        if (_isInput)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
            {
                // Fix busy-wait: if no data is available but IsSignaled is still true
                // (stale signal from previous data), reset it to prevent immediate callback
                if (!_discipline.HasDataAvailable && _discipline.DataAvailable.IsSignaled)
                    _discipline.DataAvailable.Reset();
                if (task != null)
                    return _discipline.DataAvailable.RegisterCancelable(callback, task);
                if (dispatcher?.Scheduler is { } scheduler)
                    return _discipline.DataAvailable.RegisterCancelable(callback, scheduler);
                throw new InvalidOperationException("TTY read wait requires an explicit scheduler.");
            }
        }
        else
        {
            const short POLLOUT = 0x0004;
            if ((events & POLLOUT) != 0)
                // Current TTY write wait registration has no cancellation API.
                // Fallback to bool-based registration with no-op handle.
                return _discipline.RegisterWriteWait(callback, task!.CommonKernel)
                    ? NoopWaitRegistration.Instance
                    : null;
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

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        RandomNumberGenerator.Fill(buffer);
        return buffer.Length;
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        return base.Poll(linuxFile, events);
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
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

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        // /dev/null always returns EOF.
        return 0;
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
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
