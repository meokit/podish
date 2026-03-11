namespace Fiberish.Memory;

/// <summary>
///     Ref-counted ownership handle for an externally backed page pointer.
///     Disposing the handle releases one ownership reference.
/// </summary>
public interface IPageHandle : IDisposable
{
    IntPtr Pointer { get; }
}