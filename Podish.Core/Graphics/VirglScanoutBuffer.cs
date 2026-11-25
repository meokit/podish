using Fiberish.VFS;

namespace Podish.Core.Graphics;

/// <summary>A retained VirGL texture exported directly from the host GL context.</summary>
public sealed class VirglScanoutBuffer : DrmScanoutBuffer
{
    private readonly Func<nint> _getMetalTexture;
    private readonly Action _releaseResource;

    internal VirglScanoutBuffer(uint width, uint height, uint pitch, uint format,
        Func<nint> getMetalTexture, Action releaseResource) : base(width, height, pitch)
    {
        PrimeFormat = format;
        _getMetalTexture = getMetalTexture;
        _releaseResource = releaseResource;
    }

    /// <summary>Returns the borrowed producer texture after the acquire fence has signaled.</summary>
    public nint GetMetalTexture() => _getMetalTexture();

    public override uint PrimeFormat { get; }
    public override bool Ready => true;
    public override bool Present() => false;
    protected override void Destroy() => _releaseResource();
}
