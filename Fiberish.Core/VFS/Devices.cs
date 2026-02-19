using Fiberish.Core.VFS.TTY;

namespace Fiberish.VFS;

public class ConsoleInode : Inode
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

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;

        if (_discipline != null) return _discipline.Read(buffer, linuxFile.Flags);

        return _stdin.Read(buffer);
    }

    public override async ValueTask WaitForRead(LinuxFile linuxFile)
    {
        if (!_isInput || _discipline == null) return;

        // Await the event. If already signaled, completes immediately.
        await _discipline.DataAvailable;
        
        // Reset after waking up. This ensures:
        // 1. If event was already signaled, we wake immediately and retry Read()
        // 2. If queue is still empty after retry, next WaitForRead() will block
        _discipline.DataAvailable.Reset();
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_isInput) return 0;

        if (_discipline != null) return _discipline.Write(buffer);

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
            // Output - stdout is always writable
            if ((events & POLLOUT) != 0) revents |= POLLOUT;
        }

        return revents;
    }

    public override void RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        if (_isInput && _discipline != null)
        {
            const short POLLIN = 0x0001;
            if ((events & POLLIN) != 0)
            {
                _discipline.DataAvailable.Register(callback);
            }
        }
    }

    public override void Truncate(long size)
    {
    }
}