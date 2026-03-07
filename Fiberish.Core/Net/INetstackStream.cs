namespace Fiberish.Core.Net;

public interface INetstackStream : IDisposable
{
    bool CanRead { get; }
    bool CanWrite { get; }
    bool MayRead { get; }
    bool MayWrite { get; }

    int Read(Span<byte> buffer);
    int Write(ReadOnlySpan<byte> buffer);

    void CloseWrite();
}
