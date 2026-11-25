using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

/// <summary>Linux virtio-gpu render-node UAPI used by guest Mesa Venus and VirGL.</summary>
internal sealed partial class VirtioGpuNode : Inode
{
    internal const string Name = "renderD129";
    internal const int Minor = 129;
    private const uint Version = 0xc0246400, GemClose = 0x40086409, GetCap = 0xc010640c;
    private const uint PrimeExport = 0xc00c642d, PrimeImport = 0xc00c642e;
    private const uint MapBlob = 0xc0106441, GetParam = 0xc0106443, ResourceInfo = 0xc0106445;
    private const uint WaitBlob = 0xc0086448, GetCaps = 0xc0186449, CreateBlob = 0xc030644a;
    private const uint InitContext = 0xc010644b;
    private const uint Create3D = 0xc0386444, TransferFromHost = 0xc02c6446, TransferToHost = 0xc02c6447;
    private const int Invalid = -(int)Errno.EINVAL, Fault = -(int)Errno.EFAULT, Missing = -(int)Errno.ENOENT;
    private const uint Host3D = 2;
    private const uint Mappable = 1, Shareable = 2;
    private readonly VenusRenderer _renderer;
    private readonly ILogger _logger;

    internal VirtioGpuNode(SuperBlock superBlock, VenusRenderer renderer, ILogger logger)
    {
        _renderer = renderer;
        _logger = logger;
        SuperBlock = superBlock;
        Type = InodeType.CharDev;
        Ino = 0x22681;
        Rdev = 0xe281;
        Mode = 0x1b6;
        SetInitialLinkCount(1, "Virtual virtio-gpu render node registration");
    }

    private sealed class OpenFile
    {
        public Context? Context;
        public uint NextHandle = 1;
        public long NextOffset = 0x10000;
        public readonly Dictionary<uint, Handle> Handles = [];
    }

    private sealed class Handle(Resource resource, Context? importedInto = null)
    {
        public Resource Resource { get; } = resource;
        public Context? ImportedInto { get; } = importedInto;
        public long Offset;
    }

    public override void Open(LinuxFile file)
    {
        base.Open(file);
        file.PrivateData = new OpenFile();
    }

    public override void Release(LinuxFile file)
    {
        var open = (OpenFile)file.PrivateData!;
        foreach (var handle in open.Handles.Values) ReleaseHandle(handle);
        open.Handles.Clear();
        open.Context?.Release();
        base.Release(file);
    }

    public override bool SupportsMmap => true;

    public override int ResolveMmapBacking(LinuxFile file, uint length, ref long offset, out LinuxFile backing)
    {
        backing = file;
        var open = (OpenFile)file.PrivateData!;
        foreach (var handle in open.Handles.Values)
        {
            if (handle.Offset == 0 || offset < handle.Offset || handle.Resource.Backing is not { } mapped)
                continue;
            var relative = (ulong)(offset - handle.Offset);
            if (relative + length > handle.Resource.MappedSize)
                continue;
            offset = (long)relative;
            backing = mapped;
            return 0;
        }
        return Invalid;
    }

    public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
    {
        var size = (int)((request >> 16) & 0x3fff);
        // Venus has used several append-only versions of EXECBUFFER.
        var exec = (request & 0xc000ffff) == 0xc0006442 && size is 32 or 40 or 64;
        if (!exec && request is not (Version or GemClose or GetCap or PrimeExport or PrimeImport or
                MapBlob or GetParam or ResourceInfo or WaitBlob or GetCaps or CreateBlob or InitContext or
                Create3D or TransferFromHost or TransferToHost))
        {
            _logger.LogDebug("Unsupported virtio-gpu ioctl 0x{Request:x8}", request);
            return -(int)Errno.ENOTTY;
        }

        Span<byte> data = stackalloc byte[64];
        data.Clear();
        var cpu = task.CPU;
        if (!Read(cpu, arg, data[..size])) return Fault;
        var open = (OpenFile)file.PrivateData!;
        if (exec) return Execute(open, task, arg, data, size);

        return request switch
        {
            Version => GetVersion(cpu, data) && Write(cpu, arg, data[..size]) ? 0 : Fault,
            GetCap => GetCapability(cpu, arg, data[..size]),
            GetParam => GetParameter(cpu, data),
            GetCaps => GetCapset(cpu, data),
            InitContext => InitializeContext(open, cpu, data),
            CreateBlob => AllocateBlob(open, task, arg, data[..size]),
            Create3D => AllocateVirglResource(open, task, arg, data[..size]),
            TransferFromHost => TransferVirglResource(open, task, data[..size], toHost: false),
            TransferToHost => TransferVirglResource(open, task, data[..size], toHost: true),
            ResourceInfo => GetResourceInfo(open, cpu, arg, data[..size]),
            MapBlob => GetMapOffset(open, cpu, arg, data[..size]),
            GemClose => CloseHandle(open, U32(data, 0)),
            WaitBlob => WaitForResource(open, data),
            PrimeExport => ExportPrime(open, task, arg, data[..size]),
            PrimeImport => ImportPrime(open, task, arg, data[..size]),
            _ => -(int)Errno.ENOTTY
        };
    }

    private static bool GetVersion(Engine cpu, Span<byte> data)
    {
        Put(data, 0, 0);
        Put(data, 4, 1);
        Put(data, 8, 0);
        return CopyString(cpu, data[12..], "virtio_gpu"u8) &&
               CopyString(cpu, data[20..], "0"u8) &&
               CopyString(cpu, data[28..], "virtio GPU render node"u8);
    }

    private static bool CopyString(Engine cpu, Span<byte> field, ReadOnlySpan<byte> value)
    {
        var count = (int)Math.Min(U32(field, 0), (uint)value.Length);
        var address = U32(field, 4);
        Put(field, 0, (uint)value.Length);
        return count == 0 || address == 0 || Write(cpu, address, value[..count]);
    }

    private static int GetCapability(Engine cpu, uint arg, Span<byte> data)
    {
        var value = U64(data, 0) switch
        {
            5 => 3ul,     // DRM_CAP_PRIME: import and export
            6 => 1ul,     // DRM_CAP_TIMESTAMP_MONOTONIC
            1 or 3 or 4 or 0x13 or 0x14 => 0ul,
            _ => ulong.MaxValue
        };
        if (value == ulong.MaxValue) return Invalid;
        Put64(data, 8, value);
        return Write(cpu, arg, data) ? 0 : Fault;
    }

    private int GetParameter(Engine cpu, Span<byte> data)
    {
        var value = U64(data, 0) switch
        {
            1 or 2 or 3 or 4 or 6 => 1u,
            5 => 0u,
            7 => _renderer.CapsetMask,
            _ => uint.MaxValue
        };
        if (value == uint.MaxValue) return Invalid;
        Span<byte> result = stackalloc byte[4];
        Put(result, 0, value);
        return Write(cpu, U64(data, 8), result) ? 0 : Fault;
    }

    private int GetCapset(Engine cpu, Span<byte> data)
    {
        var caps = _renderer.GetCapset(U32(data, 0), U32(data, 4));
        if (caps.IsEmpty)
            return Invalid;
        var count = (int)Math.Min(U32(data, 16), (uint)caps.Length);
        return count == 0 || Write(cpu, U64(data, 8), caps[..count]) ? 0 : Fault;
    }

    private int InitializeContext(OpenFile open, Engine cpu, Span<byte> data)
    {
        if (open.Context != null) return -(int)Errno.EEXIST;
        var count = U32(data, 0);
        if (count > 16) return Invalid;
        Span<byte> parameters = stackalloc byte[(int)count * 16];
        if (!parameters.IsEmpty && !Read(cpu, U64(data, 8), parameters)) return Fault;

        uint rings = 1;
        ulong capset = 0;
        ulong nameAddress = 0;
        for (var offset = 0; offset < parameters.Length; offset += 16)
        {
            var value = U64(parameters, offset + 8);
            switch (U64(parameters, offset))
            {
                case 1: capset = value; break;
                case 2 when value is >= 1 and <= 64: rings = (uint)value; break;
                case 3 when value == 0: break; // no read() fence events
                case 4: nameAddress = value; break;
                default: return Invalid;
            }
        }
        if (capset >= 32 || (_renderer.CapsetMask & (1u << (int)capset)) == 0 ||
            (capset != VenusRenderer.CapsetId && rings != 1))
            return Invalid;
        ReadOnlySpan<byte> name = "Podish guest"u8;
        if (nameAddress != 0)
        {
            if (nameAddress > uint.MaxValue || cpu.ReadStringSafe((uint)nameAddress, 64) is not { } text)
                return Fault;
            name = Encoding.UTF8.GetBytes(text);
        }
        open.Context = _renderer.CreateContext((uint)capset, rings, name);
        return open.Context != null ? 0 : -(int)Errno.ENOMEM;
    }

    private static int Submit(Context context, Engine cpu, ulong address, uint size)
    {
        if (size == 0) return 0;
        if (size > 64 * 1024 * 1024 || (size & 3) != 0) return Invalid;
        var bytes = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            var command = bytes.AsSpan(0, (int)size);
            if (!Read(cpu, address, command)) return Fault;
            return context.Renderer.SubmitCommand(context, command) ? 0 : Invalid;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    private int AllocateBlob(OpenFile open, FiberTask task, uint arg, Span<byte> data)
    {
        var size = U64(data, 16);
        var flags = U32(data, 4);
        if (open.Context is not { } context || U32(data, 0) != Host3D || size is 0 or > uint.MaxValue ||
            (size & LinuxConstants.PageOffsetMask) != 0 || (flags & ~(Mappable | Shareable)) != 0)
            return Invalid;
        if (context.IsVirgl) return -(int)Errno.EOPNOTSUPP;

        var rc = Submit(context, task.CPU, U64(data, 32), U32(data, 28));
        if (rc != 0) return rc;
        if (!context.Renderer.CreateResource(context.Id, U64(data, 40), size, flags, out var id, out var fd))
        {
            _logger.LogDebug("Venus rejected a {Size}-byte blob with flags {Flags}", size, flags);
            return Invalid;
        }

        var resource = new Resource(context, id, size, fd);
        if (((flags & Mappable) != 0 && fd < 0) ||
            (fd >= 0 && !resource.CreateBacking(task.Process.Syscalls)))
        {
            _logger.LogDebug("Could not map Venus blob {Id} of {Size} bytes, host fd {Fd}",
                id, size, fd);
            resource.Release();
            return Invalid;
        }

        var handle = open.NextHandle++;
        Put(data, 8, handle);
        Put(data, 12, id);
        if (!Write(task.CPU, arg, data))
        {
            resource.Release();
            return Fault;
        }
        open.Handles.Add(handle, new Handle(resource));
        return 0;
    }

    private static int CloseHandle(OpenFile open, uint handle)
    {
        if (!open.Handles.Remove(handle, out var entry)) return Invalid;
        ReleaseHandle(entry);
        return 0;
    }

    private static void ReleaseHandle(Handle entry)
    {
        if (entry.ImportedInto is { } context)
            context.Renderer.DetachResource(context, entry.Resource.Id);
        entry.Resource.Release();
    }

    private static int GetResourceInfo(OpenFile open, Engine cpu, uint arg, Span<byte> data)
    {
        if (!open.Handles.TryGetValue(U32(data, 0), out var entry)) return Missing;
        Put(data, 4, entry.Resource.Id);
        Put(data, 8, (uint)entry.Resource.Size);
        Put(data, 12, entry.Resource.IsVirgl ? 0u : Host3D);
        return Write(cpu, arg, data) ? 0 : Fault;
    }

    private static int GetMapOffset(OpenFile open, Engine cpu, uint arg, Span<byte> data)
    {
        if (!open.Handles.TryGetValue(U32(data, 8), out var entry)) return Missing;
        if (entry.Resource.Backing == null) return Invalid;
        if (entry.Offset == 0)
        {
            var next = open.NextOffset + (long)entry.Resource.MappedSize;
            if (next > ((long)uint.MaxValue + 1) * LinuxConstants.PageSize)
                return -(int)Errno.ENOSPC;
            entry.Offset = open.NextOffset;
            open.NextOffset = next;
        }
        Put64(data, 0, (ulong)entry.Offset);
        return Write(cpu, arg, data) ? 0 : Fault;
    }

    private static int WaitForResource(OpenFile open, Span<byte> data)
    {
        if (!open.Handles.TryGetValue(U32(data, 0), out var entry)) return Missing;
        var fence = entry.Resource.LastFence;
        if (fence == null || fence.Signaled) return 0;
        return U32(data, 4) == 0 ? fence.Wait() : -(int)Errno.EBUSY;
    }

    private static bool Read(Engine cpu, ulong address, Span<byte> bytes) =>
        address is > 0 and <= uint.MaxValue && address + (uint)bytes.Length <= 1UL << 32 &&
        cpu.CopyFromUser((uint)address, bytes);

    private static bool Write(Engine cpu, ulong address, ReadOnlySpan<byte> bytes) =>
        address is > 0 and <= uint.MaxValue && address + (uint)bytes.Length <= 1UL << 32 &&
        cpu.CopyToUser((uint)address, bytes);

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    private static void Put(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    private static void Put64(Span<byte> data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data[offset..], value);
}
