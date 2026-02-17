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

        await _discipline.DataAvailable;
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_isInput) return 0;

        if (_discipline != null) return _discipline.Write(buffer);

        _stdout.Write(buffer);
        _stdout.Flush();
        return buffer.Length;
    }

    public override void Truncate(long size)
    {
    }
}