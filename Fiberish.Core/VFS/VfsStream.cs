using Fiberish.Core;
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
    private bool _disposed;
    private readonly TaskAsyncOperationHandle? _fileLifetime;

    public VfsStream(LinuxFile file)
        : this(file, default(FileMutationContext))
    {
    }

    public VfsStream(LinuxFile file, FileMutationContext mutationContext)
    {
        _file = file;
        _mutationContext = mutationContext;
        _position = file.Position;
    }

    internal VfsStream(LinuxFile file, FiberTask task)
        : this(file, default(FileMutationContext))
    {
        // Retirement can cancel the I/O without resuming the awaiter.
        if (!task.TryEnterAsyncOperation(null, out _fileLifetime) || _fileLifetime == null)
        {
            file.Close();
            throw new OperationCanceledException();
        }

        if (!_fileLifetime.TryAddRegistration(TaskAsyncRegistration.From(file)))
            throw new OperationCanceledException();
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
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_file.OpenedInode == null) return 0;

        // ELF header/program-header stack buffers can be read directly, instead
        // of Stream.Read(Span)'s pooled array and additional copy.
        var n = _file.OpenedInode!.ReadToHost(null, _file, buffer, _position);
        if (n < 0)
            throw new IOException(BuildIoFailureMessage("read", n, buffer.Length));
        if (n > 0) _position += n;
        return n;
    }

    internal async ValueTask ReadExactlyForTaskAsync(FiberTask task, Memory<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var count = await ReadForTaskAsync(task, buffer);
            if (count == 0) throw new EndOfStreamException();
            buffer = buffer[count..];
        }
    }

    internal async ValueTask<int> ReadForTaskAsync(FiberTask task, Memory<byte> buffer)
    {
        if (_file.OpenedInode is not { } inode) return 0;
        var n = await inode.ReadToHostAsync(task, _file, buffer, _position);
        if (n < 0)
            throw new ExecFileException((Errno)(-n), BuildIoFailureMessage("read", n, buffer.Length));
        _position += n;
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_fileLifetime != null)
                _fileLifetime.TryCompleteWithoutScheduling();
            else
                _file.Close();
        }

        base.Dispose(disposing);
    }
}
