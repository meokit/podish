using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class AddressSpace : MemoryObject
{
    public AddressSpace(MemoryObjectKind kind, LinuxFile? file, long fileBaseOffset, long fileSize, bool shared,
        MemoryObjectRole role = MemoryObjectRole.FileSharedSource)
        : base(kind, file, fileBaseOffset, fileSize, shared, role)
    {
    }
}

public sealed class AnonVma : MemoryObject
{
    public AnonVma(MemoryObjectKind kind, LinuxFile? file, long fileBaseOffset, long fileSize, bool shared,
        MemoryObjectRole role)
        : base(kind, file, fileBaseOffset, fileSize, shared, role)
    {
    }
}