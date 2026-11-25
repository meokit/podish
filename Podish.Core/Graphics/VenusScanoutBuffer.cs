using Fiberish.VFS;

namespace Podish.Core.Graphics;

/// <summary>A Venus LINEAR PRIME plane whose backing remains open until the last display owner releases it.</summary>
public sealed class VenusScanoutBuffer : DrmScanoutBuffer
{
    private readonly Action _releaseResource;

    internal VenusScanoutBuffer(uint width, uint height, uint pitch, uint offset, uint format,
        int hostFd, ulong backingSize, Action releaseResource)
        : base(width, height, pitch)
    {
        Offset = offset;
        PrimeFormat = format;
        HostFd = hostFd;
        BackingSize = backingSize;
        _releaseResource = releaseResource;
    }

    /// <summary>Borrowed Darwin file descriptor valid while the caller holds a buffer reference.</summary>
    public int HostFd { get; }
    public ulong BackingSize { get; }
    public uint Offset { get; }
    public override uint PrimeFormat { get; }
    public override bool Ready => true;
    // Wayland hands the backing to its GPU presenter after the acquire fence signals.
    public override bool Present() => false;
    protected override void Destroy() => _releaseResource();
}
