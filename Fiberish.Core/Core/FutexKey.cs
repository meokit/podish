using System.Runtime.CompilerServices;
using Fiberish.Memory;
using Fiberish.VFS;

namespace Fiberish.Core;

internal enum FutexKeyKind : byte
{
    Private,
    SharedFile,
    SharedAnonymous
}

internal readonly struct FutexKey : IEquatable<FutexKey>
{
    private readonly object _identity;

    private FutexKey(FutexKeyKind kind, object identity, uint pageValue, ushort offsetWithinPage)
    {
        Kind = kind;
        _identity = identity;
        PageValue = pageValue;
        OffsetWithinPage = offsetWithinPage;
    }

    public FutexKeyKind Kind { get; }
    public uint PageValue { get; }
    public ushort OffsetWithinPage { get; }

    public static FutexKey Private(VMAManager addressSpace, uint pageAlignedAddress, ushort offsetWithinPage)
    {
        return new FutexKey(FutexKeyKind.Private, addressSpace, pageAlignedAddress, offsetWithinPage);
    }

    public static FutexKey SharedFile(Inode inode, uint pageIndex, ushort offsetWithinPage)
    {
        return new FutexKey(FutexKeyKind.SharedFile, inode, pageIndex, offsetWithinPage);
    }

    public static FutexKey SharedAnonymous(AddressSpace backingObject, uint pageIndex, ushort offsetWithinPage)
    {
        return new FutexKey(FutexKeyKind.SharedAnonymous, backingObject, pageIndex, offsetWithinPage);
    }

    public void AcquireRef()
    {
        switch (Kind)
        {
            case FutexKeyKind.Private:
                ((VMAManager)_identity).AcquireFutexKeyRef();
                break;
            case FutexKeyKind.SharedFile:
                ((Inode)_identity).AcquireRef(InodeRefKind.KernelInternal, "FutexKey");
                break;
            case FutexKeyKind.SharedAnonymous:
                ((AddressSpace)_identity).AddRef();
                break;
        }
    }

    public void ReleaseRef()
    {
        switch (Kind)
        {
            case FutexKeyKind.Private:
                ((VMAManager)_identity).ReleaseFutexKeyRef();
                break;
            case FutexKeyKind.SharedFile:
                ((Inode)_identity).ReleaseRef(InodeRefKind.KernelInternal, "FutexKey");
                break;
            case FutexKeyKind.SharedAnonymous:
                ((AddressSpace)_identity).Release();
                break;
        }
    }

    public bool Equals(FutexKey other)
    {
        return Kind == other.Kind &&
               PageValue == other.PageValue &&
               OffsetWithinPage == other.OffsetWithinPage &&
               ReferenceEquals(_identity, other._identity);
    }

    public override bool Equals(object? obj)
    {
        return obj is FutexKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine((int)Kind, PageValue, OffsetWithinPage, RuntimeHelpers.GetHashCode(_identity));
    }
}