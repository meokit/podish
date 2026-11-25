using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

/// <summary>In-process Venus and optional VirGL renderers for the Apple virtio-gpu device.</summary>
internal sealed unsafe class VenusRenderer
{
    private static readonly Lock Gate = new();
    private static VenusRenderer? _instance;

    private readonly nint _library;
    private readonly Callbacks* _callbacks;
    private volatile VirglRenderer? _virgl;

    private readonly delegate* unmanaged[Cdecl]<uint, uint, uint, byte*, byte> _createContext;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _destroyContext;
    private readonly delegate* unmanaged[Cdecl]<uint, byte*, uint, byte> _submitCommand;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, byte> _submitFence;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, uint, int*, int*, uint*, void*, byte> _createResource;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, int, int, ulong, byte> _importResource;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _destroyResource;
    private readonly delegate* unmanaged[Cdecl]<int, long> _sharedFdSize;

    private readonly ConcurrentDictionary<uint, VirtioGpuNode.Context> _contexts = new();
    private uint _nextContext;
    private uint _nextResource;

    private VenusRenderer(nint library)
    {
        _library = library;
        nint Symbol(string name) => NativeLibrary.GetExport(library, name);

        _createContext = (delegate* unmanaged[Cdecl]<uint, uint, uint, byte*, byte>)Symbol("podish_vkr_renderer_create_context");
        _destroyContext = (delegate* unmanaged[Cdecl]<uint, void>)Symbol("podish_vkr_renderer_destroy_context");
        _submitCommand = (delegate* unmanaged[Cdecl]<uint, byte*, uint, byte>)Symbol("podish_vkr_renderer_submit_cmd");
        _submitFence = (delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, byte>)Symbol("podish_vkr_renderer_submit_fence");
        _createResource = (delegate* unmanaged[Cdecl]<uint, uint, ulong, ulong, uint, int*, int*, uint*, void*, byte>)
            Symbol("podish_vkr_renderer_create_resource");
        _importResource = (delegate* unmanaged[Cdecl]<uint, uint, int, int, ulong, byte>)Symbol("podish_vkr_renderer_import_resource");
        _destroyResource = (delegate* unmanaged[Cdecl]<uint, uint, void>)Symbol("podish_vkr_renderer_destroy_resource");
        _sharedFdSize = (delegate* unmanaged[Cdecl]<int, long>)Symbol("podish_venus_shared_fd_size");

        var getCapset = (delegate* unmanaged[Cdecl]<byte*, uint, nuint>)Symbol("podish_vkr_get_capset");
        var size = getCapset(null, 0);
        if (size is 0 or > 4096)
            throw new InvalidOperationException("Unexpected Venus capset size.");
        Capset = new byte[(int)size];
        fixed (byte* capset = Capset)
            getCapset(capset, 0);

        var initialize = (delegate* unmanaged[Cdecl]<uint, Callbacks*, byte>)Symbol("podish_vkr_renderer_init");
        var callbacks = (Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Callbacks));
        callbacks->RetireFence = &RetireFence;
        // The renderer retains the callback table and reports from a native sync thread.
        if (initialize(3, callbacks) == 0)
        {
            NativeMemory.Free(callbacks);
            throw new InvalidOperationException("Venus renderer initialization failed.");
        }
        _callbacks = callbacks;
    }

    internal const uint CapsetId = 4;
    internal const int SharedMemoryFd = 2;
    private byte[] Capset { get; }
    internal uint CapsetMask => (1u << (int)CapsetId) | (_virgl?.CapsetMask ?? 0);
    internal bool CanExportVirglTexture => _virgl?.CanExportTexture == true;

    internal ReadOnlySpan<byte> GetCapset(uint capsetId, uint version)
    {
        if (version != 0) return [];
        if (capsetId == CapsetId) return Capset;
        return _virgl is { } virgl ? virgl.GetCapset(capsetId) : [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public nint DebugLogger;
        public delegate* unmanaged[Cdecl]<uint, uint, ulong, void> RetireFence;
    }

    internal static VenusRenderer? TryLoad(string path, ILogger logger, bool enableVirgl = false)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) ||
            !File.Exists(path))
            return null;

        lock (Gate)
        {
            if (_instance != null)
            {
                if (enableVirgl) _instance.EnableVirgl(logger);
                return _instance;
            }
            nint library = 0;
            try
            {
                library = NativeLibrary.Load(path);
                var version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "podish_venus_abi_version");
                var probe = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "podish_venus_probe");
                if (version() != 2)
                    throw new InvalidOperationException("The PodishVenus framework ABI does not match this build.");
                if (probe() != 1)
                    throw new InvalidOperationException("No Metal 4 Vulkan device found.");
                _instance = new VenusRenderer(library);
                logger.LogInformation("Initialized the Apple Venus Vulkan renderer");
                if (enableVirgl) _instance.EnableVirgl(logger);
                return _instance;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                       BadImageFormatException or InvalidOperationException)
            {
                if (library != 0) NativeLibrary.Free(library);
                logger.LogWarning(ex, "Could not initialize the Apple Venus Vulkan renderer");
                return null;
            }
        }
    }

    private void EnableVirgl(ILogger logger)
    {
        if (_virgl != null) return;
        try
        {
            _virgl = new VirglRenderer(_library, (nint)_callbacks);
            logger.LogInformation("Initialized the Apple VirGL renderer");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not initialize the optional Apple VirGL renderer");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RetireFence(uint context, uint ring, ulong sequence)
    {
        if (_instance?._contexts.TryGetValue(context, out var value) == true)
            value.Retire(ring, sequence);
    }

    internal VirtioGpuNode.Context? CreateContext(uint capset, uint rings, ReadOnlySpan<byte> name)
    {
        lock (Gate)
        {
            var id = ++_nextContext;
            fixed (byte* bytes = name)
            {
                var result = capset == CapsetId
                    ? _createContext(id, capset, (uint)name.Length, bytes)
                    : _virgl!.CreateContext(id, capset, (uint)name.Length, bytes);
                if (result == 0)
                    return null;
            }

            var context = new VirtioGpuNode.Context(this, id, rings, capset);
            _contexts.TryAdd(id, context);
            return context;
        }
    }

    internal void DestroyContext(VirtioGpuNode.Context context)
    {
        lock (Gate)
        {
            _contexts.TryRemove(context.Id, out _);
            if (context.IsVirgl) _virgl!.DestroyContext(context.Id);
            else _destroyContext(context.Id);
        }
    }

    internal bool SubmitCommand(VirtioGpuNode.Context context, Span<byte> command)
    {
        lock (Gate)
        {
            fixed (byte* bytes = command)
                return (context.IsVirgl ? _virgl!.SubmitCommand : _submitCommand)(context.Id, bytes, (uint)command.Length) != 0;
        }
    }

    internal bool SubmitFence(VirtioGpuNode.Context context, uint ring, ulong sequence)
    {
        lock (Gate)
            return (context.IsVirgl ? _virgl!.SubmitFence : _submitFence)(context.Id, 1, ring, sequence) != 0;
    }

    internal bool CreateResource(uint context, ulong blob, ulong size, uint flags, out uint id, out int fd)
    {
        lock (Gate)
        {
            id = ++_nextResource;
            var type = -1;
            var resourceFd = -1;
            uint mapInfo;
            ulong* vulkanInfo = stackalloc ulong[8];
            var ok = _createResource(context, id, blob, size, flags, &type, &resourceFd, &mapInfo, vulkanInfo) != 0;
            fd = ok && type == SharedMemoryFd ? resourceFd : -1;
            if (fd < 0 && resourceFd >= 0) Close(resourceFd);
            return ok;
        }
    }

    internal bool CreateVirglResource(uint context, ReadOnlySpan<byte> arguments, out uint id, out int fd)
    {
        lock (Gate)
        {
            id = ++_nextResource;
            int resourceFd = -1;
            bool created;
            fixed (byte* data = arguments)
                created = _virgl!.CreateResource(context, id, data, &resourceFd) != 0;
            if (!created && resourceFd >= 0)
            {
                Close(resourceFd);
                resourceFd = -1;
            }
            fd = resourceFd;
            return created;
        }
    }

    internal bool TransferVirglResource(uint context, uint id, bool toHost, ReadOnlySpan<byte> arguments)
    {
        lock (Gate)
            fixed (byte* data = arguments)
                return _virgl!.Transfer(context, id, toHost ? (byte)1 : (byte)0, data) != 0;
    }

    internal nint ExportVirglTexture(uint context, uint id)
    {
        lock (Gate)
            return _virgl is { CanExportTexture: true } virgl ? virgl.ExportTexture(context, id) : 0;
    }

    internal bool ImportResource(VirtioGpuNode.Context context, uint id, int fd, ulong size)
    {
        lock (Gate)
            return context.IsVirgl ? _virgl!.AttachResource(context.Id, id) != 0 :
                _importResource(context.Id, id, SharedMemoryFd, fd, size) != 0;
    }

    internal void DetachResource(VirtioGpuNode.Context context, uint id)
    {
        lock (Gate)
        {
            if (context.IsVirgl) _virgl!.DetachResource(context.Id, id);
            else _destroyResource(context.Id, id);
        }
    }

    internal void DestroyResource(VirtioGpuNode.Context context, uint id)
    {
        lock (Gate)
        {
            if (context.IsVirgl) _virgl!.DestroyResource(context.Id, id);
            else _destroyResource(context.Id, id);
        }
    }

    internal long GetSharedFdSize(int fd) => _sharedFdSize(fd);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close")]
    internal static extern int Close(int fd);
}
