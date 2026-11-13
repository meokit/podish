namespace Fiberish.Memory;

public readonly record struct FilePageIdentity(ulong DeviceId, ulong InodeNumber, uint PageIndex)
{
    public bool IsValid => InodeNumber != 0;

    public override string ToString() => $"[dev={DeviceId}, ino={InodeNumber}, page={PageIndex}]";
}
