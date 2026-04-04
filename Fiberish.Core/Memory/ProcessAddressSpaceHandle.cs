using Fiberish.Core;

namespace Fiberish.Memory;

internal sealed class ProcessAddressSpaceHandle : IDisposable
{
    private MmuHandle _mmu;

    private ProcessAddressSpaceHandle(MmuHandle mmu)
    {
        _mmu = mmu;
    }

    public nuint Identity => _mmu.Identity;

    public void Dispose()
    {
        _mmu.Dispose();
    }

    internal static ProcessAddressSpaceHandle CaptureAttachedEngine(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new ProcessAddressSpaceHandle(engine.CaptureCurrentMmuHandle());
    }

    internal static ProcessAddressSpaceHandle DetachFromSharedEngine(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        using var detached = engine.DetachOwnedMmuHandle();
        return new ProcessAddressSpaceHandle(engine.CaptureCurrentMmuHandle());
    }

    internal void AttachEngine(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.AttachOwnedMmuHandle(_mmu);
    }

    internal bool IsAttachedTo(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.CurrentMmuIdentityInternal == Identity;
    }
}
