using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;

namespace Fiberish.Tests;

internal sealed class TestRuntimeFactory
{
    public TestRuntimeFactory()
        : this(new MemoryRuntimeContext())
    {
    }

    public TestRuntimeFactory(HostMemoryMapGeometry hostMemoryMapGeometry)
        : this(new MemoryRuntimeContext(hostMemoryMapGeometry))
    {
    }

    public TestRuntimeFactory(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        MemoryContext = memoryContext;
    }

    public MemoryRuntimeContext MemoryContext { get; }

    public Engine CreateEngine()
    {
        return new Engine(MemoryContext);
    }

    public VMAManager CreateAddressSpace()
    {
        return new VMAManager(MemoryContext);
    }

    public Process CreateOwnedProcess(int pid, SyscallManager? syscalls = null, UTSNamespace? uts = null)
    {
        return CreateProcess(pid, MemoryContext, syscalls, uts);
    }

    public static Process CreateProcess(
        int pid,
        SyscallManager? syscalls = null,
        UTSNamespace? uts = null)
    {
        return CreateProcess(pid, syscalls?.MemoryContext ?? new MemoryRuntimeContext(), syscalls, uts);
    }

    public static Process CreateProcess(
        int pid,
        MemoryRuntimeContext memoryContext,
        SyscallManager? syscalls = null,
        UTSNamespace? uts = null)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        return new Process(pid, new VMAManager(memoryContext), syscalls!, uts);
    }
}
