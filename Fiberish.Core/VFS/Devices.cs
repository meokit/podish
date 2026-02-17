namespace Fiberish.VFS;

public class ConsoleInode : Inode
{
    private readonly bool _isInput;

    public ConsoleInode(SuperBlock sb, bool isInput)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 666
        _isInput = isInput;
        Ino = 1; // Dummy
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

    private static readonly Stream _stdout = Console.OpenStandardOutput();
    private static readonly Stream _stdin = Console.OpenStandardInput();

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;
        return _stdin.Read(buffer);
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_isInput) return 0;
        _stdout.Write(buffer);
        _stdout.Flush();
        return buffer.Length;
    }

    public override void Truncate(long size)
    {
    }
}