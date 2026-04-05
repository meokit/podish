namespace Fiberish.Memory;

public sealed class MemoryRuntimeContext
{
    public MemoryRuntimeContext()
        : this(HostMemoryMapGeometry.CreateCurrent())
    {
    }

    public MemoryRuntimeContext(HostMemoryMapGeometry hostMemoryMapGeometry)
    {
        HostMemoryMapGeometry = hostMemoryMapGeometry;
    }

    public static MemoryRuntimeContext Default { get; } = new();

    public HostMemoryMapGeometry HostMemoryMapGeometry { get; }
}