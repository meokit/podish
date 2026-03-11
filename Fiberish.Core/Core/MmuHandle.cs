using Microsoft.Win32.SafeHandles;
using X86Native = Fiberish.X86.Native.X86Native;

namespace Fiberish.Core;

public sealed class MmuHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MmuHandle()
        : base(true)
    {
    }

    internal MmuHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    public nuint Identity
    {
        get
        {
            ThrowIfInvalid();
            return X86Native.MmuGetIdentity(handle);
        }
    }

    public static MmuHandle CreateEmpty()
    {
        var handle = X86Native.MmuCreateEmpty();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create empty MMU handle.");
        return new MmuHandle(handle);
    }

    public MmuHandle CloneSkipExternal()
    {
        ThrowIfInvalid();
        var cloned = X86Native.MmuCloneSkipExternal(handle);
        if (cloned == IntPtr.Zero)
            throw new InvalidOperationException("Failed to clone MMU handle.");
        return new MmuHandle(cloned);
    }

    public MmuHandle AddRefHandle()
    {
        ThrowIfInvalid();
        var retained = X86Native.MmuRetain(handle);
        if (retained == IntPtr.Zero)
            throw new InvalidOperationException("Failed to retain MMU handle.");
        return new MmuHandle(retained);
    }

    internal IntPtr DangerousMmuHandle()
    {
        ThrowIfInvalid();
        return handle;
    }

    internal void ThrowIfInvalid()
    {
        if (IsInvalid || IsClosed)
            throw new ObjectDisposedException(nameof(MmuHandle), "MMU handle is no longer valid.");
    }

    protected override bool ReleaseHandle()
    {
        X86Native.MmuRelease(handle);
        return true;
    }
}