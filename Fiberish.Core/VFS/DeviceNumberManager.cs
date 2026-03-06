using System.Numerics;

namespace Fiberish.VFS;

/// <summary>
/// Manages device number (dev_t) allocation in Linux style.
/// Encodes device numbers as (major << 8) | minor.
/// Per-SyscallManager instance to isolate device number spaces.
/// </summary>
public sealed class DeviceNumberManager
{
    // Default major used for regular filesystems (SCSI disk major)
    public const int DefaultMajor = 8;
    public const int MaxMinorPerMajor = 256;
    public const int MinorsPerUlong = 64; // 64 bits per ulong

    // major -> bitmap of allocated minors
    // Each ulong holds 64 minors, so we need 4 ulongs per major (256/64)
    private readonly Dictionary<int, ulong[]> _minorMaps = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Allocates a new device number with the default major.
    /// If the default major is exhausted, automatically allocates from next available major.
    /// </summary>
    public uint Allocate(int preferredMajor = DefaultMajor)
    {
        using (_lock.EnterScope())
        {
            // Try preferred major first
            if (TryAllocateFromMajor(preferredMajor, out var minor))
                return Encode(preferredMajor, minor);

            // Fall back to any available major
            for (int major = 1; major < 256; major++)
            {
                if (TryAllocateFromMajor(major, out minor))
                    return Encode(major, minor);
            }

            throw new InvalidOperationException("No available device numbers (all majors exhausted)");
        }
    }

    /// <summary>
    /// Frees a previously allocated device number.
    /// Idempotent: freeing unallocated or already-freed dev is a no-op.
    /// </summary>
    public void Free(uint dev)
    {
        if (dev == 0) return; // Anonymous device

        var (major, minor) = Decode(dev);

        using (_lock.EnterScope())
        {
            if (_minorMaps.TryGetValue(major, out var bitmap))
            {
                int ulongIndex = minor / MinorsPerUlong;
                int bitIndex = minor % MinorsPerUlong;

                if (ulongIndex < bitmap.Length)
                    bitmap[ulongIndex] &= ~(1UL << bitIndex);
            }
        }
    }

    /// <summary>
    /// Encodes major:minor into device number.
    /// </summary>
    public static uint Encode(int major, int minor) =>
        (uint)((major & 0xFF) << 8) | (uint)(minor & 0xFF);

    /// <summary>
    /// Decodes device number into (major, minor).
    /// </summary>
    public static (int major, int minor) Decode(uint dev) =>
        ((int)(dev >> 8) & 0xFF, (int)(dev & 0xFF));

    public static int Major(uint dev) => (int)(dev >> 8) & 0xFF;
    public static int Minor(uint dev) => (int)(dev & 0xFF);

    private bool TryAllocateFromMajor(int major, out int minor)
    {
        if (!_minorMaps.TryGetValue(major, out var bitmap))
        {
            bitmap = new ulong[MaxMinorPerMajor / MinorsPerUlong]; // 4 ulongs = 256 bits
            _minorMaps[major] = bitmap;
        }

        for (int i = 0; i < bitmap.Length; i++)
        {
            ulong word = bitmap[i];
            if (word == ~0UL) continue; // All 64 bits set, fully allocated

            // Find first zero bit using hardware instruction if available
            int firstZero = BitOperations.TrailingZeroCount(~word);
            if (firstZero < MinorsPerUlong)
            {
                bitmap[i] |= 1UL << firstZero;
                minor = i * MinorsPerUlong + firstZero;
                return true;
            }
        }

        minor = -1;
        return false;
    }
}
