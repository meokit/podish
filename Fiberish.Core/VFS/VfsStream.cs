using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>
///     A Stream implementation that wraps a VFS LinuxFile.
///     This allows standard .NET libraries (like LibObjectFile) to read/write VFS files.
/// </summary>
public class VfsStream : Stream
{
    private readonly LinuxFile _file;
    private readonly FileMutationContext _mutationContext;
    private long _position;

    public VfsStream(LinuxFile file)
        : this(file, default)
    {
    }

    public VfsStream(LinuxFile file, FileMutationContext mutationContext)
    {
        _file = file;
        _mutationContext = mutationContext;
        _position = file.Position;
    }

    public override bool CanRead => (_file.Flags & FileFlags.O_WRONLY) == 0;
    public override bool CanSeek => true;
    public override bool CanWrite => (_file.Flags & (FileFlags.O_WRONLY | FileFlags.O_RDWR)) != 0;

    public override long Length => (long)(_file.OpenedInode?.Size ?? 0);

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _file.OpenedInode?.Sync(_file);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_file.OpenedInode == null) return 0;

        var n = _file.OpenedInode!.ReadToHost(null, _file, buffer.AsSpan(offset, count), _position);
        if (n < 0)
            throw new IOException(BuildIoFailureMessage("read", n, count));
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
        if (!_mutationContext.HasLiveAddressSpace &&
            _file.OpenedInode is MappingBackedInode mappingBackedInode &&
            value < (long)mappingBackedInode.Size &&
            mappingBackedInode.SnapshotMappedAddressSpaces().Length > 0)
            throw new InvalidOperationException("VfsStream.SetLength requires FileMutationContext for live mapped shrink operations.");

        _file.OpenedInode?.Truncate(value, _mutationContext);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_file.OpenedInode == null) return;

        var n = _file.OpenedInode!.WriteFromHost(null, _file, buffer.AsSpan(offset, count), _position);
        if (n < 0)
            throw new IOException(BuildIoFailureMessage("write", n, count));
        if (n > 0) _position += n;
    }

    private string BuildIoFailureMessage(string operation, int result, int requestedCount)
    {
        var errnoValue = -result;
        var errnoName = Enum.IsDefined(typeof(Errno), errnoValue)
            ? ((Errno)errnoValue).ToString()
            : errnoValue.ToString();
        return
            $"VFS {operation} failed: errno={errnoName} result={result} position={_position} requestedCount={requestedCount}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Close();

        base.Dispose(disposing);
    }
}
