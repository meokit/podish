using System.Runtime.InteropServices;
using Fiberish.VFS;

namespace Fiberish.Memory;

/// <summary>
///     Ownership record for an externally backed page pointer.
///     This is a value type and must be released through <see cref="Release(ref BackingPageHandle)" /> on the owner slot.
/// </summary>
public struct BackingPageHandle
{
    public IntPtr Pointer;
    internal long ReleaseToken;
    internal Inode? ReleaseOwner;

    public readonly bool IsValid => Pointer != IntPtr.Zero;

    public static BackingPageHandle CreateNative(IntPtr pointer)
    {
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                ReleaseToken = pointer.ToInt64()
            };
    }

    public static BackingPageHandle CreateOwned(IntPtr pointer, Inode releaseOwner, long releaseToken)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                ReleaseOwner = releaseOwner,
                ReleaseToken = releaseToken
            };
    }

    public static void Release(ref BackingPageHandle handle)
    {
        var releasedPtr = Interlocked.Exchange(ref handle.Pointer, IntPtr.Zero);
        if (releasedPtr == IntPtr.Zero)
            return;

        var releaseOwner = handle.ReleaseOwner;
        var releaseToken = handle.ReleaseToken;
        handle.ReleaseOwner = null;
        handle.ReleaseToken = 0;

        if (releaseOwner != null)
        {
            releaseOwner.ReleaseMappedPageHandle(releaseToken);
            return;
        }

        unsafe
        {
            NativeMemory.AlignedFree((void*)releaseToken);
        }
    }
}