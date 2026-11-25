using System.Runtime.InteropServices;

namespace Podish.Core.Graphics;

/// <summary>Optional native VirGL backend in the Apple renderer framework.</summary>
internal sealed unsafe class VirglRenderer
{
    private readonly byte[] _caps1;
    private readonly byte[] _caps2;
    private readonly delegate* unmanaged[Cdecl]<byte> _canExport;

    internal readonly delegate* unmanaged[Cdecl]<uint, uint, uint, byte*, byte> CreateContext;
    internal readonly delegate* unmanaged[Cdecl]<uint, void> DestroyContext;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, byte*, int*, byte> CreateResource;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, void> DestroyResource;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, byte> AttachResource;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, void> DetachResource;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, byte, byte*, byte> Transfer;
    internal readonly delegate* unmanaged[Cdecl]<uint, byte*, uint, byte> SubmitCommand;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, byte> SubmitFence;
    internal readonly delegate* unmanaged[Cdecl]<uint, uint, nint> ExportTexture;

    internal VirglRenderer(nint library, nint callbacks)
    {
        nint Symbol(string name) => NativeLibrary.GetExport(library, "podish_virgl_" + name);

        var initialize = (delegate* unmanaged[Cdecl]<nint, byte>)Symbol("init");
        var getCaps = (delegate* unmanaged[Cdecl]<uint, uint, byte*, uint, uint>)Symbol("get_caps");
        CreateContext = (delegate* unmanaged[Cdecl]<uint, uint, uint, byte*, byte>)Symbol("create_context");
        DestroyContext = (delegate* unmanaged[Cdecl]<uint, void>)Symbol("destroy_context");
        CreateResource = (delegate* unmanaged[Cdecl]<uint, uint, byte*, int*, byte>)Symbol("resource_create");
        DestroyResource = (delegate* unmanaged[Cdecl]<uint, uint, void>)Symbol("resource_destroy");
        AttachResource = (delegate* unmanaged[Cdecl]<uint, uint, byte>)Symbol("resource_attach");
        DetachResource = (delegate* unmanaged[Cdecl]<uint, uint, void>)Symbol("resource_detach");
        Transfer = (delegate* unmanaged[Cdecl]<uint, uint, byte, byte*, byte>)Symbol("transfer");
        SubmitCommand = (delegate* unmanaged[Cdecl]<uint, byte*, uint, byte>)Symbol("submit_cmd");
        SubmitFence = (delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, byte>)Symbol("submit_fence");
        if (NativeLibrary.TryGetExport(library, "podish_virgl_export_resource", out var export))
            ExportTexture = (delegate* unmanaged[Cdecl]<uint, uint, nint>)export;
        if (NativeLibrary.TryGetExport(library, "podish_virgl_can_export", out var canExport))
            _canExport = (delegate* unmanaged[Cdecl]<byte>)canExport;

        if (initialize(callbacks) == 0)
            throw new InvalidOperationException("VirGL renderer initialization failed.");

        _caps1 = ReadCaps(getCaps, 1);
        _caps2 = ReadCaps(getCaps, 2);
        if (CapsetMask == 0)
            throw new InvalidOperationException("The VirGL renderer supplied no capsets.");
    }

    internal uint CapsetMask => (_caps1.Length != 0 ? 1u << 1 : 0) | (_caps2.Length != 0 ? 1u << 2 : 0);
    internal bool CanExportTexture => ExportTexture != null && (_canExport == null || _canExport() != 0);

    internal ReadOnlySpan<byte> GetCapset(uint id) => id switch
    {
        1 => _caps1,
        2 => _caps2,
        _ => []
    };

    private static byte[] ReadCaps(delegate* unmanaged[Cdecl]<uint, uint, byte*, uint, uint> getCaps, uint id)
    {
        const uint version = 0; // The Linux VirGL winsys always requests version 0.
        uint size = getCaps(id, version, null, 0);
        if (size == 0) return [];
        if (size > 64 * 1024) throw new InvalidOperationException("Unexpected VirGL capset size.");

        var data = new byte[size];
        fixed (byte* buffer = data)
            return getCaps(id, version, buffer, size) == size ? data : [];
    }
}
