namespace Fiberish.Syscalls;

internal sealed class UnixSockaddrInfo
{
    public required byte[] SunPathRaw { get; init; }
    public required bool IsAbstract { get; init; }
    public byte[] PathBytes { get; init; } = [];
    public string AbstractKey { get; init; } = "";
}
