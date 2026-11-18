namespace Fiberish.VFS;

/// <summary>A GPU buffer owned by a rendering backend, with references held by DRM handles and framebuffers.</summary>
public abstract class DrmScanoutBuffer(uint width, uint height, uint pitch)
{
    private int _references = 1;
    private int _scanoutPins;

    public uint Width { get; } = width;
    public uint Height { get; } = height;
    public uint Pitch { get; } = pitch;
    public bool InUse => _scanoutPins != 0 || GuestLocked;
    internal bool GuestLocked { get; set; }

    public abstract bool Ready { get; }
    /// <summary>Present this buffer and complete the host GPU's use before returning.</summary>
    public abstract bool Present();
    protected abstract void Destroy();

    internal void Retain() => _references++;
    public void Release()
    {
        if (--_references == 0) Destroy();
    }

    internal void Pin() => _scanoutPins++;
    internal void Unpin() => _scanoutPins--;
}
