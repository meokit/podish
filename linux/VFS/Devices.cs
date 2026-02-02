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

    public override Inode? Lookup(string name) => null;
    public override Inode Create(string name, int mode, int uid, int gid) => throw new InvalidOperationException("Cannot create in /dev");
    public override Inode Mkdir(string name, int mode, int uid, int gid) => throw new InvalidOperationException("Cannot mkdir in /dev");
    public override void Unlink(string name) { }
    public override void Rmdir(string name) { }
    
    public override int Read(File file, Span<byte> buffer, long offset)
    {
        if (!_isInput) return 0;
        // Console.OpenStandardInput().Read... but Console.In is easier?
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
    public override List<DirectoryEntry> GetEntries() => new();
}
