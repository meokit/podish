using System;
using System.IO;

namespace Bifrost.Syscalls;

public abstract class LinuxFile : IDisposable
{
    public abstract string Name { get; }
    public abstract long Position { get; set; }
    public abstract int Read(Span<byte> buffer);
    public abstract int Write(ReadOnlySpan<byte> buffer);
    public abstract void Flush();
    public abstract void Dispose();
}

public class LinuxFileStream : LinuxFile
{
    private readonly FileStream _fs;
    public LinuxFileStream(FileStream fs) => _fs = fs;
    public FileStream Stream => _fs;
    public override string Name => _fs.Name;
    public override long Position { get => _fs.Position; set => _fs.Position = value; }
    public override int Read(Span<byte> buffer) => _fs.Read(buffer);
    public override int Write(ReadOnlySpan<byte> buffer) 
    { 
        _fs.Write(buffer); 
        return buffer.Length; 
    }
    public override void Flush() => _fs.Flush();
    public override void Dispose() => _fs.Dispose();
}

public class LinuxDirectory : LinuxFile
{
    public override string Name { get; }
    public override long Position { get; set; } = 0;
    public LinuxDirectory(string path) => Name = path;
    public override int Read(Span<byte> buffer) => 0;
    public override int Write(ReadOnlySpan<byte> buffer) => 0;
    public override void Flush() { }
    public override void Dispose() { }
}

public class LinuxStandardStream : LinuxFile
{
    private readonly Stream _s;
    private readonly string _name;
    public LinuxStandardStream(Stream s, string name) { _s = s; _name = name; }
    public override string Name => _name;
    public override long Position { get => 0; set { } }
    public override int Read(Span<byte> buffer) => _s.Read(buffer);
    public override int Write(ReadOnlySpan<byte> buffer) { _s.Write(buffer); return buffer.Length; }
    public override void Flush() => _s.Flush();
    public override void Dispose() { }
}
