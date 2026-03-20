namespace Fiberish.Memory;

public sealed class MemoryRuntimeContext
{
    public static MemoryRuntimeContext Default { get; } = new();

    public MemoryRuntimeContext()
        : this(HostMemoryMapGeometry.CreateCurrent())
    {
    }

    public MemoryRuntimeContext(HostMemoryMapGeometry hostMemoryMapGeometry)
    {
        HostMemoryMapGeometry = hostMemoryMapGeometry;
    }

    public HostMemoryMapGeometry HostMemoryMapGeometry { get; }
}
