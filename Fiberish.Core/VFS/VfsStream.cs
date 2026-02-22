using Fiberish.Native;
using System.IO;

namespace Fiberish.VFS;

/// <summary>
/// A Stream implementation that wraps a VFS LinuxFile.
/// This allows standard .NET libraries (like LibObjectFile) to read/write VFS files.
/// </summary>
public class VfsStream : Stream
{
    private readonly LinuxFile _file;
    private long _position;

    public VfsStream(LinuxFile file)
    {
        _file = file;
        _position = file.Position;
    }

    public override bool CanRead => (_file.Flags & FileFlags.O_WRONLY) == 0;
    public override bool CanSeek => true;
    public override bool CanWrite => (_file.Flags & (FileFlags.O_WRONLY | FileFlags.O_RDWR)) != 0;

    public override long Length => (long)(_file.Dentry.Inode?.Size ?? 0);

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _file.Dentry.Inode?.Sync(_file);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_file.Dentry.Inode == null) return 0;
        
        var n = _file.Dentry.Inode.Read(_file, buffer.AsSpan(offset, count), _position);
        if (n > 0) _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin:
                _position = offset;
                break;
            case SeekOrigin.Current:
                _position += offset;
                break;
            case SeekOrigin.End:
                _position = Length + offset;
                break;
        }
        return _position;
    }

    public override void SetLength(long value)
    {
        _file.Dentry.Inode?.Truncate(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_file.Dentry.Inode == null) return;
        
        var n = _file.Dentry.Inode.Write(_file, buffer.AsSpan(offset, count), _position);
        if (n > 0) _position += n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Close();
        }
        base.Dispose(disposing);
    }
}
