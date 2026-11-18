using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>A Linux framebuffer whose video memory is shared with guest mappings.</summary>
public sealed class FramebufferDevice
{
    public const int DimensionLimit = 4096;
    public int MaximumWidth { get; }
    public int MaximumHeight { get; }
    public VirtualConsoleInput Input { get; } = new();

    private readonly Lock _gate = new();
    private readonly int _videoMemoryLength;
    private readonly byte[] _row;
    private readonly ushort[][] _palette = [new ushort[256], new ushort[256], new ushort[256], new ushort[256]];
    private FramebufferInode? _inode;
    private FramebufferMode _mode;
    private bool _blanked;

    public FramebufferDevice(int width = 640, int height = 480, int bitsPerPixel = 32)
    {
        if (width is < 1 or > DimensionLimit)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > DimensionLimit)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (bitsPerPixel is not (8 or 15 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerPixel));

        MaximumWidth = width;
        MaximumHeight = height;
        // Reserve enough memory for two full-size 32-bit buffers, even after a mode change.
        var bytes = width * height * 8;
        _videoMemoryLength = (bytes + LinuxConstants.PageSize - 1) & -LinuxConstants.PageSize;
        _row = new byte[width * 4];
        _mode = new(width, height, width, height, 0, 0, bitsPerPixel);
        for (var i = 0; i < 256; i++)
        {
            _palette[0][i] = _palette[1][i] = _palette[2][i] = (ushort)(i * 257);
            _palette[3][i] = ushort.MaxValue;
        }
    }

    internal FramebufferInode Attach(SuperBlock sb)
    {
        lock (_gate)
        {
            if (_inode != null)
                throw new InvalidOperationException("The framebuffer is already attached.");
            _inode = new FramebufferInode(sb, this, _videoMemoryLength);
            return _inode;
        }
    }

    internal void Detach(FramebufferInode inode)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_inode, inode))
                _inode = null;
        }
    }

    internal void StopPresentation()
    {
        lock (_gate)
            _inode = null;
        Input.Detach();
    }

    public (int Width, int Height) VisibleSize
    {
        get { lock (_gate) return (_mode.Width, _mode.Height); }
    }

    /// <summary>Writes a bottom-up RGBA frame to the visible portion of video memory.</summary>
    public bool TryPresentRgba(ReadOnlySpan<byte> rgba, int width, int height, bool scale)
    {
        lock (_gate)
        {
            var mode = _mode;
            if (_inode == null || width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4 ||
                (!scale && (width != mode.Width || height != mode.Height)))
                return false;

            if (mode.Depth == 8)
            {
                // Install an RGB332 palette for the indexed scanout.
                for (var i = 0; i < 256; i++)
                {
                    _palette[0][i] = (ushort)((i >> 5) * 65535 / 7);
                    _palette[1][i] = (ushort)(((i >> 2) & 7) * 65535 / 7);
                    _palette[2][i] = (ushort)((i & 3) * 65535 / 3);
                }
            }

            var row = _row.AsSpan(0, mode.Width * mode.PixelBytes);
            Span<int> columns = stackalloc int[mode.Width];
            for (var x = 0; x < mode.Width; x++)
                columns[x] = x * width / mode.Width * 4;

            for (var y = 0; y < mode.Height; y++)
            {
                var source = rgba.Slice((height - 1 - y * height / mode.Height) * width * 4, width * 4);
                for (var x = 0; x < mode.Width; x++)
                {
                    var r = source[columns[x]];
                    var g = source[columns[x] + 1];
                    var b = source[columns[x] + 2];
                    var offset = x * mode.PixelBytes;
                    switch (mode.Depth)
                    {
                        case 8:
                            row[offset] = (byte)((r & 0xE0) | ((g >> 3) & 0x1C) | (b >> 6));
                            break;
                        case 15:
                            BinaryPrimitives.WriteUInt16LittleEndian(row[offset..],
                                (ushort)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)));
                            break;
                        case 16:
                            BinaryPrimitives.WriteUInt16LittleEndian(row[offset..],
                                (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)));
                            break;
                        default:
                            row[offset] = b;
                            row[offset + 1] = g;
                            row[offset + 2] = r;
                            if (mode.Depth == 32) row[offset + 3] = 0xff;
                            break;
                    }
                }
                var position = (y + mode.YOffset) * mode.Stride + mode.XOffset * mode.PixelBytes;
                if (_inode.WriteVideoMemory(null, row, position) != row.Length) return false;
            }
            return true;
        }
    }

    internal bool TryPresentXrgb8888(LinuxFile source, int width, int height, int pitch)
    {
        lock (_gate)
        {
            var mode = _mode;
            if (_inode == null || mode.Depth != 32 || mode.Width != width || mode.Height != height ||
                source.OpenedInode == null)
                return false;

            var row = _row.AsSpan(0, width * 4);
            for (var y = 0; y < height; y++)
            {
                if (source.OpenedInode.ReadToHost(null, source, row, (long)y * pitch) != row.Length)
                    return false;
                var position = (y + mode.YOffset) * mode.Stride + mode.XOffset * 4;
                if (_inode.WriteVideoMemory(null, row, position) != row.Length)
                    return false;
            }
            return true;
        }
    }

    /// <summary>Copies the visible framebuffer as tightly packed, little-endian XRGB8888.</summary>
    public bool TryCopyFrame(Span<byte> xrgb8888, out int width, out int height)
    {
        lock (_gate)
        {
            var mode = _mode;
            width = mode.Width;
            height = mode.Height;
            if (_inode == null || xrgb8888.Length < width * height * 4)
                return false;

            var frame = xrgb8888[..(width * height * 4)];
            if (_blanked)
            {
                frame.Clear();
                return true;
            }

            var row = _row.AsSpan(0, width * mode.PixelBytes);
            for (var y = 0; y < height; y++)
            {
                var offset = (y + mode.YOffset) * mode.Stride + mode.XOffset * mode.PixelBytes;
                var destination = frame.Slice(y * width * 4, width * 4);
                if (mode.Depth == 32)
                    _inode.CopyVideoMemory(offset, destination);
                else
                {
                    _inode.CopyVideoMemory(offset, row);
                    ConvertRow(mode.Depth, row, destination);
                }
            }

            return true;
        }
    }

    private void ConvertRow(int depth, ReadOnlySpan<byte> row, Span<byte> destination)
    {
        if (depth == 24)
        {
            for (int source = 0, target = 0; source < row.Length; source += 3, target += 4)
                row.Slice(source, 3).CopyTo(destination[target..]);
        }
        else if (depth == 8)
        {
            for (var x = 0; x < row.Length; x++)
            {
                var index = row[x];
                destination[x * 4] = (byte)(_palette[2][index] >> 8);
                destination[x * 4 + 1] = (byte)(_palette[1][index] >> 8);
                destination[x * 4 + 2] = (byte)(_palette[0][index] >> 8);
            }
        }
        else
        {
            var greenBits = depth == 15 ? 5 : 6;
            var greenMask = (1 << greenBits) - 1;
            for (var x = 0; x < row.Length / 2; x++)
            {
                var pixel = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                destination[x * 4] = ExpandColor(pixel & 31, 5);
                destination[x * 4 + 1] = ExpandColor((pixel >> 5) & greenMask, greenBits);
                destination[x * 4 + 2] = ExpandColor((pixel >> (5 + greenBits)) & 31, 5);
            }
        }

        for (var x = 3; x < destination.Length; x += 4)
            destination[x] = 0;
    }

    private static byte ExpandColor(int value, int bits) => (byte)(value * 255 / ((1 << bits) - 1));

    internal int Ioctl(Engine cpu, uint request, uint arg)
    {
        lock (_gate)
        {
            return request switch
            {
                0x4600 => GetVariableInfo(cpu, arg),       // FBIOGET_VSCREENINFO
                0x4601 => SetVariableInfo(cpu, arg),       // FBIOPUT_VSCREENINFO
                0x4602 => GetFixedInfo(cpu, arg),          // FBIOGET_FSCREENINFO
                0x4604 => TransferPalette(cpu, arg, false), // FBIOGETCMAP
                0x4605 => TransferPalette(cpu, arg, true),  // FBIOPUTCMAP
                0x4606 => PanDisplay(cpu, arg),            // FBIOPAN_DISPLAY
                0x460f => GetConsoleMapping(cpu, arg),     // FBIOGET_CON2FBMAP
                0x4611 => SetBlank(arg),                   // FBIOBLANK
                _ => -(int)Errno.ENOTTY
            };
        }
    }

    private int GetFixedInfo(Engine cpu, uint arg)
    {
        // IA-32 fb_fix_screeninfo, including its two-byte trailing alignment padding.
        Span<byte> data = stackalloc byte[68];
        data.Clear();
        "Podish fbdev"u8.CopyTo(data);
        Put32(data, 20, _videoMemoryLength);
        Put32(data, 32, _mode.Depth == 8 ? 3 : 2); // Pseudocolor / truecolor
        BinaryPrimitives.WriteUInt16LittleEndian(data[36..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data[38..], 1);
        Put32(data, 44, _mode.Stride);
        return cpu.CopyToUser(arg, data) ? 0 : -(int)Errno.EFAULT;
    }

    private int GetVariableInfo(Engine cpu, uint arg)
    {
        Span<byte> data = stackalloc byte[160];
        WriteVariableInfo(data, _mode, 0);
        return cpu.CopyToUser(arg, data) ? 0 : -(int)Errno.EFAULT;
    }

    private static void WriteVariableInfo(Span<byte> data, FramebufferMode mode, int activate)
    {
        data.Clear();
        Put32(data, 0, mode.Width);
        Put32(data, 4, mode.Height);
        Put32(data, 8, mode.VirtualWidth);
        Put32(data, 12, mode.VirtualHeight);
        Put32(data, 16, mode.XOffset);
        Put32(data, 20, mode.YOffset);
        Put32(data, 24, mode.Depth == 15 ? 16 : mode.Depth);
        switch (mode.Depth)
        {
            case 8:
                Put32(data, 36, 8);
                Put32(data, 48, 8);
                Put32(data, 60, 8);
                break;
            case 15:
            case 16:
                var greenBits = mode.Depth == 15 ? 5 : 6;
                Put32(data, 32, 5 + greenBits);
                Put32(data, 36, 5);
                Put32(data, 44, 5);
                Put32(data, 48, greenBits);
                Put32(data, 60, 5);
                break;
            default:
                Put32(data, 32, 16);
                Put32(data, 36, 8);
                Put32(data, 44, 8);
                Put32(data, 48, 8);
                Put32(data, 60, 8);
                break;
        }

        Put32(data, 84, activate);
        Put32(data, 88, -1); // Physical dimensions are unknown.
        Put32(data, 92, -1);
    }

    private int SetVariableInfo(Engine cpu, uint arg)
    {
        Span<byte> data = stackalloc byte[160];
        if (!cpu.CopyFromUser(arg, data)) return -(int)Errno.EFAULT;

        var width = Get32(data, 0);
        var height = Get32(data, 4);
        var requestedVirtualWidth = Get32(data, 8);
        var virtualWidth = Math.Max(width, requestedVirtualWidth);
        var requestedVirtualHeight = Get32(data, 12);
        var virtualHeight = Math.Max(height, requestedVirtualHeight);
        var xOffset = Get32(data, 16);
        var yOffset = Get32(data, 20);
        var depth = Get32(data, 24);
        var activate = Get32(data, 84);
        if (depth == 16 && Get32(data, 48) == 5 && Get32(data, 32) == 10)
            depth = 15;

        const int activateMask = 15;
        const int activateTest = 2;
        if (width <= 0 || width > MaximumWidth || height <= 0 || height > MaximumHeight ||
            requestedVirtualWidth < 0 || requestedVirtualHeight < 0 ||
            xOffset < 0 || xOffset > virtualWidth - width || yOffset < 0 || yOffset > virtualHeight - height ||
            depth is not (8 or 15 or 16 or 24 or 32) || Get32(data, 28) != 0 || Get32(data, 80) != 0 ||
            (Get32(data, 132) & 0x100) != 0 || (activate & activateMask) is not (0 or activateTest))
            return -(int)Errno.EINVAL;

        var mode = new FramebufferMode(width, height, virtualWidth, virtualHeight, xOffset, yOffset, depth);
        if (virtualWidth > _videoMemoryLength / mode.PixelBytes ||
            virtualHeight > _videoMemoryLength / mode.Stride)
            return -(int)Errno.EINVAL;

        WriteVariableInfo(data, mode, activate);
        if (!cpu.CopyToUser(arg, data)) return -(int)Errno.EFAULT;
        if ((activate & activateMask) != activateTest)
            _mode = mode;
        return 0;
    }

    private int PanDisplay(Engine cpu, uint arg)
    {
        Span<byte> data = stackalloc byte[160];
        if (!cpu.CopyFromUser(arg, data)) return -(int)Errno.EFAULT;
        var x = Get32(data, 16);
        var y = Get32(data, 20);
        if (x < 0 || x > _mode.VirtualWidth - _mode.Width ||
            y < 0 || y > _mode.VirtualHeight - _mode.Height || (Get32(data, 132) & 0x100) != 0)
            return -(int)Errno.EINVAL;

        _mode = _mode with { XOffset = x, YOffset = y };
        return 0;
    }

    private int TransferPalette(Engine cpu, uint arg, bool put)
    {
        Span<byte> cmap = stackalloc byte[24];
        if (!cpu.CopyFromUser(arg, cmap)) return -(int)Errno.EFAULT;
        var start = Get32(cmap, 0);
        var length = Get32(cmap, 4);
        if (start < 0 || start > 256 || length < 0 || length > 256 - start)
            return -(int)Errno.EINVAL;
        if (length == 0) return 0;

        Span<byte> channels = stackalloc byte[4 * 256 * sizeof(ushort)];
        for (var channel = 0; channel < 4; channel++)
        {
            var address = (uint)Get32(cmap, 8 + channel * 4);
            if (channel == 3 && address == 0)
                break;
            if (address == 0)
                return -(int)Errno.EFAULT;
            var bytes = channels.Slice(channel * length * 2, length * 2);
            if (put)
            {
                if (!cpu.CopyFromUser(address, bytes)) return -(int)Errno.EFAULT;
            }
            else
            {
                for (var i = 0; i < length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes[(i * 2)..], _palette[channel][start + i]);
                if (!cpu.CopyToUser(address, bytes)) return -(int)Errno.EFAULT;
            }
        }

        if (put)
        {
            var channelCount = Get32(cmap, 20) == 0 ? 3 : 4;
            for (var channel = 0; channel < channelCount; channel++)
                for (var i = 0; i < length; i++)
                    _palette[channel][start + i] =
                        BinaryPrimitives.ReadUInt16LittleEndian(channels[((channel * length + i) * 2)..]);
        }

        return 0;
    }

    private static int GetConsoleMapping(Engine cpu, uint arg)
    {
        Span<byte> mapping = stackalloc byte[8];
        if (!cpu.CopyFromUser(arg, mapping)) return -(int)Errno.EFAULT;
        if (Get32(mapping, 0) != 1) return -(int)Errno.EINVAL;
        Put32(mapping, 4, 0);
        return cpu.CopyToUser(arg, mapping) ? 0 : -(int)Errno.EFAULT;
    }

    private int SetBlank(uint arg)
    {
        if (arg > 4) return -(int)Errno.EINVAL;
        _blanked = arg != 0;
        return 0;
    }

    private static int Get32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    private static void Put32(Span<byte> data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data[offset..], value);

    private readonly record struct FramebufferMode(
        int Width, int Height, int VirtualWidth, int VirtualHeight, int XOffset, int YOffset, int Depth)
    {
        public int PixelBytes => (Depth + 7) / 8;
        public int Stride => VirtualWidth * PixelBytes;
    }
}

internal sealed class FramebufferInode : MappingBackedInode
{
    private static long _nextIno = 0x130000;
    private readonly FramebufferDevice _device;

    public FramebufferInode(SuperBlock sb, FramebufferDevice device, int length)
    {
        SuperBlock = sb;
        _device = device;
        Type = InodeType.CharDev;
        Ino = (ulong)Interlocked.Increment(ref _nextIno);
        Mode = 0x1b6;
        Rdev = 0x1d00; // Major 29, minor 0.
        Size = (ulong)length;
        SetInitialLinkCount(1, "framebuffer registration");
    }

    public override bool SupportsMmap => true;
    protected override AddressSpaceKind MappingKind => AddressSpaceKind.Shmem;
    // Video memory must remain resident when guests unmap it or invoke msync.
    protected override AddressSpacePolicy.AddressSpaceCacheClass? MappingCacheClass => null;

    public override int ResolveMmapBacking(LinuxFile file, uint length, ref long offset, out LinuxFile backing)
    {
        backing = file;
        return offset >= 0 && (ulong)offset + length <= Size ? 0 : -(int)Errno.EINVAL;
    }

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg) =>
        _device.Ioctl(task.CPU, request, arg);

    internal unsafe void CopyVideoMemory(int offset, Span<byte> destination)
    {
        var mapping = Mapping;
        while (!destination.IsEmpty)
        {
            var pageOffset = offset & (int)LinuxConstants.PageOffsetMask;
            var size = Math.Min(destination.Length, LinuxConstants.PageSize - pageOffset);
            var page = mapping?.PeekPage((uint)(offset / LinuxConstants.PageSize)) ?? IntPtr.Zero;
            if (page == IntPtr.Zero)
                destination[..size].Clear();
            else
                new ReadOnlySpan<byte>((void*)(page + pageOffset), size).CopyTo(destination);

            offset += size;
            destination = destination[size..];
        }
    }

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        if (offset < 0) return -(int)Errno.EINVAL;
        var length = (int)Math.Min(buffer.Length, Math.Max(0, (long)Size - offset));
        if (length > 0)
            CopyVideoMemory((int)offset, buffer[..length]);
        return length;
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset) =>
        WriteVideoMemory(linuxFile, buffer, offset);

    internal unsafe int WriteVideoMemory(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (offset < 0) return -(int)Errno.EINVAL;
        if (buffer.IsEmpty) return 0;
        if ((ulong)offset >= Size) return -(int)Errno.ENOSPC;
        var length = (int)Math.Min(buffer.Length, (long)Size - offset);
        var consumed = 0;
        while (consumed < length)
        {
            var position = offset + consumed;
            var pageIndex = (uint)(position / LinuxConstants.PageSize);
            var pageOffset = (int)position & (int)LinuxConstants.PageOffsetMask;
            var size = Math.Min(length - consumed, LinuxConstants.PageSize - pageOffset);
            var page = AcquireMappingPage(linuxFile, pageIndex, position - pageOffset,
                PageCacheAccessMode.Write, 0);
            if (page == IntPtr.Zero) return consumed > 0 ? consumed : -(int)Errno.ENOMEM;
            buffer.Slice(consumed, size).CopyTo(new Span<byte>((void*)(page + pageOffset), size));
            consumed += size;
        }

        return consumed;
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task, ArraySegment<Iovec> iovs,
        long offset, int flags) => ValueTask.FromResult(Transfer(engine, file, iovs, offset, false));

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task, ArraySegment<Iovec> iovs,
        long offset, int flags) => ValueTask.FromResult(Transfer(engine, file, iovs, offset, true));

    private int Transfer(Engine engine, LinuxFile file, ArraySegment<Iovec> iovs, long offset, bool write)
    {
        var start = offset == -1 ? file.Position : offset;
        if (start < 0) return -(int)Errno.EINVAL;
        var total = 0;
        Span<byte> buffer = stackalloc byte[LinuxConstants.PageSize];
        foreach (var iov in iovs)
        {
            uint transferred = 0;
            while (transferred < iov.Len)
            {
                var position = start + total;
                var remaining = (long)Size - position;
                if (remaining <= 0)
                    return total == 0 && write ? -(int)Errno.ENOSPC : total;
                var address = iov.BaseAddr + transferred;
                if (address < iov.BaseAddr)
                    return total > 0 ? total : -(int)Errno.EFAULT;
                var count = (int)Math.Min(Math.Min(iov.Len - transferred,
                    LinuxConstants.PageSize - (address & LinuxConstants.PageOffsetMask)), remaining);
                var data = buffer[..count];
                int result;
                if (write)
                {
                    if (!engine.CopyFromUser(address, data))
                        return total > 0 ? total : -(int)Errno.EFAULT;
                    result = WriteFromHost(null, file, data, position);
                }
                else
                {
                    result = ReadToHost(null, file, data, position);
                    if (result > 0 && !engine.CopyToUser(address, data[..result]))
                        return total > 0 ? total : -(int)Errno.EFAULT;
                }

                if (result <= 0) return total > 0 ? total : result;
                total += result;
                transferred += (uint)result;
                if (offset == -1)
                    file.Position = start + total;
            }
        }

        return total;
    }

    internal override int SyncMappedPage(LinuxFile? linuxFile, AddressSpace mapping,
        PageSyncRequest request, InodePageRecord record) => 0;

    protected override void OnEvictCache()
    {
        _device.Detach(this);
        base.OnEvictCache();
    }
}
