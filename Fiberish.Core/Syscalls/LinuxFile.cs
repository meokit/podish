namespace Fiberish.Syscalls;

public abstract class LinuxFile : IDisposable
{
    public abstract string Name { get; }
    public abstract long Position { get; set; }
    public abstract void Dispose();
    public abstract int Read(Span<byte> buffer);
    public abstract int Write(ReadOnlySpan<byte> buffer);
    public abstract void Flush();
}

public class LinuxFileStream(FileStream fs) : LinuxFile
{
    public FileStream Stream { get; } = fs;

    public override string Name => Stream.Name;

    public override long Position
    {
        get => Stream.Position;
        set => Stream.Position = value;
    }

    public override int Read(Span<byte> buffer)
    {
        return Stream.Read(buffer);
    }

    public override int Write(ReadOnlySpan<byte> buffer)
    {
        Stream.Write(buffer);
        return buffer.Length;
    }

    public override void Flush()
    {
        Stream.Flush();
    }

    public override void Dispose()
    {
        Stream.Dispose();
    }
}

public class LinuxDirectory(string path) : LinuxFile
{
    public override string Name { get; } = path;
    public override long Position { get; set; } = 0;

    public override int Read(Span<byte> buffer)
    {
        return 0;
    }

    public override int Write(ReadOnlySpan<byte> buffer)
    {
        return 0;
    }

    public override void Flush()
    {
    }

    public override void Dispose()
    {
    }
}

public class LinuxStandardStream(Stream s, string name) : LinuxFile
{
    private readonly string _name = name;
    private readonly Stream _s = s;

    public override string Name => _name;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override int Read(Span<byte> buffer)
    {
        return _s.Read(buffer);
    }

    public override int Write(ReadOnlySpan<byte> buffer)
    {
        _s.Write(buffer);
        return buffer.Length;
    }

    public override void Flush()
    {
        _s.Flush();
    }

    public override void Dispose()
    {
    }
}