using System;
using System.Collections.Generic;

namespace Bifrost.VFS;

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

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid) => throw new InvalidOperationException("Cannot create in /dev");
    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid) => throw new InvalidOperationException("Cannot mkdir in /dev");
    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid) => throw new InvalidOperationException("Cannot symlink in /dev");
    public override Dentry Link(Dentry dentry, Inode oldInode) => throw new InvalidOperationException("Cannot link in /dev");

    public override int Read(File file, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;
        // Using OpenStandardInput for binary compatibility
        using var stream = Console.OpenStandardInput();
        return stream.Read(buffer);
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_isInput) return 0;
        using var stream = Console.OpenStandardOutput();
        stream.Write(buffer);
        return buffer.Length;
    }

    public override void Truncate(long size) { }
}
