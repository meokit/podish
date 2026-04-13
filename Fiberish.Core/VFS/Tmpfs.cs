using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

public class Tmpfs : FileSystem
{
    public Tmpfs(DeviceNumberManager? devManager = null, MemoryRuntimeContext? memoryContext = null)
        : base(devManager, memoryContext)
    {
        Name = "tmpfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var sizeLimitBytes = ParseSizeLimitBytes(data);
        var sb = new TmpfsSuperBlock(fsType, DevManager, sizeLimitBytes, MemoryContext);
        var rootInode = sb.AllocInode();
        rootInode.Type = InodeType.Directory;
        rootInode.Mode = 0x1FF;
        rootInode.SetInitialLinkCount(2, "Tmpfs.ReadSuper.root");

        sb.Root = new Dentry(FsName.Empty, rootInode, null, sb);
        sb.Root.Parent = sb.Root;

        return sb;
    }

    private static long ParseSizeLimitBytes(object? data)
    {
        if (data is not string options || string.IsNullOrWhiteSpace(options))
            return 0;

        var limit = 0L;
        var tokens = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (!token.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = token[5..].Trim();
            limit = ParseSizeValue(value);
        }

        return limit;
    }

    private static long ParseSizeValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("tmpfs size= option is empty");

        var s = raw.Trim().ToLowerInvariant();
        long multiplier = 1;

        char? unit = null;
        if (s.EndsWith("ib", StringComparison.Ordinal) && s.Length > 2)
        {
            unit = s[^3];
            s = s[..^2];
        }
        else if (s.EndsWith('b') && s.Length > 1 && char.IsLetter(s[^2]))
        {
            unit = s[^2];
            s = s[..^1];
        }
        else if (char.IsLetter(s[^1]))
        {
            unit = s[^1];
            s = s[..^1];
        }

        if (unit.HasValue)
        {
            multiplier = unit.Value switch
            {
                'k' => 1024L,
                'm' => 1024L * 1024L,
                'g' => 1024L * 1024L * 1024L,
                't' => 1024L * 1024L * 1024L * 1024L,
                _ => throw new FormatException($"Unsupported tmpfs size suffix: {unit.Value}")
            };

            if (s.EndsWith("i", StringComparison.Ordinal))
                s = s[..^1];
        }

        if (!long.TryParse(s, out var value) || value < 0)
            throw new FormatException($"Invalid tmpfs size value: {raw}");

        return checked(value * multiplier);
    }
}

public class TmpfsSuperBlock : IndexedMemorySuperBlock
{
    private long _usedDataBytes;

    public TmpfsSuperBlock(FileSystemType type, DeviceNumberManager devManager, long sizeLimitBytes = 0,
        MemoryRuntimeContext? memoryContext = null) : base(type, devManager, memoryContext ?? new MemoryRuntimeContext())
    {
        SizeLimitBytes = sizeLimitBytes;
    }

    public long SizeLimitBytes { get; }

    public long UsedDataBytes
    {
        get
        {
            lock (Lock)
            {
                return _usedDataBytes;
            }
        }
    }

    public bool TryReserveDataBytes(long bytes)
    {
        if (bytes <= 0) return true;
        lock (Lock)
        {
            if (SizeLimitBytes > 0 && _usedDataBytes > SizeLimitBytes - bytes)
                return false;
            _usedDataBytes += bytes;
            return true;
        }
    }

    public void ReleaseDataBytes(long bytes)
    {
        if (bytes <= 0) return;
        lock (Lock)
        {
            _usedDataBytes -= bytes;
            if (_usedDataBytes < 0)
                _usedDataBytes = 0;
        }
    }

    protected override IndexedMemoryInode CreateIndexedInode(ulong ino)
    {
        return new TmpfsInode(ino, this);
    }
}

public class TmpfsInode : IndexedMemoryInode
{
    private const uint F_SEAL_SEAL = 0x0001;
    private const uint F_SEAL_SHRINK = 0x0002;
    private const uint F_SEAL_GROW = 0x0004;
    private const uint F_SEAL_WRITE = 0x0008;
    private const uint KnownSealMask = F_SEAL_SEAL | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE;

    public TmpfsInode(ulong ino, SuperBlock sb) : base(ino, (IndexedMemorySuperBlock)sb)
    {
    }

    protected override bool PinNamespaceDentries => true;

    public bool IsMemfd { get; set; }
    public bool AllowSealing { get; set; }
    public uint SealFlags { get; private set; } = F_SEAL_SEAL;
    public bool IsMemfdExecutable { get; private set; }
    public bool IsMemfdNoExecSealed { get; private set; }
    public string MemfdDisplayName { get; private set; } = "anon";

    private TmpfsSuperBlock TmpfsSb => (TmpfsSuperBlock)SuperBlock;

    public void InitializeMemfd(string displayName, bool allowSealing, bool executable = false, bool noExecSeal = false)
    {
        IsMemfd = true;
        AllowSealing = allowSealing;
        SealFlags = allowSealing ? 0u : F_SEAL_SEAL;
        IsMemfdExecutable = executable;
        IsMemfdNoExecSealed = noExecSeal;
        MemfdDisplayName = string.IsNullOrEmpty(displayName) ? "anon" : displayName;
    }

    public int AddSeals(uint seals)
    {
        if (!IsMemfd)
            return -(int)Errno.EINVAL;
        if ((seals & ~KnownSealMask) != 0)
            return -(int)Errno.EINVAL;
        if (!AllowSealing || (SealFlags & F_SEAL_SEAL) != 0)
            return -(int)Errno.EPERM;

        SealFlags |= seals;
        return 0;
    }

    public int GetSeals()
    {
        return IsMemfd ? (int)SealFlags : -(int)Errno.EINVAL;
    }

    protected override int WriteToPageCache(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type != InodeType.File)
            return base.WriteToPageCache(linuxFile, buffer, offset);
        if (offset < 0)
            return -(int)Errno.EINVAL;

        long endOffset;
        try
        {
            endOffset = checked(offset + buffer.Length);
        }
        catch (OverflowException)
        {
            return -(int)Errno.EFBIG;
        }

        if ((SealFlags & F_SEAL_WRITE) != 0)
            return -(int)Errno.EPERM;

        var oldSize = (long)Size;
        var growth = Math.Max(0L, endOffset - oldSize);
        if (growth > 0 && (SealFlags & F_SEAL_GROW) != 0)
            return -(int)Errno.EPERM;
        if (growth > 0 && !TmpfsSb.TryReserveDataBytes(growth))
            return -(int)Errno.ENOSPC;

        try
        {
            var rc = base.WriteToPageCache(linuxFile, buffer, offset);
            if (rc < 0 && growth > 0)
                TmpfsSb.ReleaseDataBytes(growth);
            return rc;
        }
        catch
        {
            if (growth > 0)
                TmpfsSb.ReleaseDataBytes(growth);
            throw;
        }
    }

    public override int Truncate(long size)
    {
        if (Type != InodeType.File)
            return base.Truncate(size);
        if (size < 0)
            return -(int)Errno.EINVAL;

        var oldSize = (long)Size;
        if (size > oldSize && (SealFlags & F_SEAL_GROW) != 0)
            return -(int)Errno.EPERM;
        if (size < oldSize && (SealFlags & F_SEAL_SHRINK) != 0)
            return -(int)Errno.EPERM;
        if (size > oldSize)
        {
            var growth = size - oldSize;
            if (!TmpfsSb.TryReserveDataBytes(growth))
                return -(int)Errno.ENOSPC;

            try
            {
                var rc = base.Truncate(size);
                if (rc < 0)
                    TmpfsSb.ReleaseDataBytes(growth);
                return rc;
            }
            catch
            {
                TmpfsSb.ReleaseDataBytes(growth);
                throw;
            }
        }

        var shrink = oldSize - size;
        var result = base.Truncate(size);
        if (result == 0 && shrink > 0)
            TmpfsSb.ReleaseDataBytes(shrink);
        return result;
    }

    protected override void OnFinalizeDelete()
    {
        if (Type == InodeType.File && Size > 0)
        {
            TmpfsSb.ReleaseDataBytes((long)Size);
            Size = 0;
        }

        base.OnFinalizeDelete();
    }
}
