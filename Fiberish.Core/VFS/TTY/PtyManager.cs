using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core.VFS.TTY;

/// <summary>
///     Manages the allocation and lifecycle of PTY (Pseudo-Terminal) pairs.
///     Each PTY consists of a master side (ptmx) and a slave side (pts/N).
/// </summary>
public class PtyManager
{
    /// <summary>
    ///     Major device number for PTY master (ptmx).
    /// </summary>
    public const uint PTMX_MAJOR = 5;

    /// <summary>
    ///     Minor device number for PTY master (ptmx).
    /// </summary>
    public const uint PTMX_MINOR = 2;

    /// <summary>
    ///     Major device number for PTY slaves (pts/N).
    /// </summary>
    public const uint PTS_MAJOR = 136;

    /// <summary>
    ///     Maximum number of PTYs that can be allocated.
    /// </summary>
    public const int MAX_PTYS = 1024;

    private readonly ILogger _logger;
    private KernelScheduler? _scheduler;

    private readonly ConcurrentDictionary<int, PtyPair> _ptyPairs = new();
    private int _nextPtyIndex;

    public PtyManager(ILogger logger, KernelScheduler? scheduler = null)
    {
        _logger = logger;
        _scheduler = scheduler;
    }

    public void BindScheduler(KernelScheduler scheduler)
    {
        _scheduler ??= scheduler;
    }

    /// <summary>
    ///     Event raised when a new PTY slave is created (for devpts to handle).
    /// </summary>
    public event Action<int, PtyPair>? OnPtyCreated;

    /// <summary>
    ///     Event raised when a PTY is destroyed (for devpts to handle).
    /// </summary>
    public event Action<int>? OnPtyDestroyed;

    /// <summary>
    ///     Encodes a device number from major and minor numbers.
    /// </summary>
    public static uint EncodeRdev(uint major, uint minor)
    {
        return (major << 8) | minor;
    }

    /// <summary>
    ///     Decodes a device number into major and minor numbers.
    /// </summary>
    public static (uint Major, uint Minor) DecodeRdev(uint rdev)
    {
        return ((rdev >> 8) & 0xFF, rdev & 0xFF);
    }

    /// <summary>
    ///     Gets the device number (rdev) for the PTY master (ptmx).
    /// </summary>
    public static uint GetPtmxRdev()
    {
        return EncodeRdev(PTMX_MAJOR, PTMX_MINOR);
    }

    /// <summary>
    ///     Gets the device number (rdev) for a PTY slave.
    /// </summary>
    public static uint GetPtsRdev(int ptyIndex)
    {
        return EncodeRdev(PTS_MAJOR, (uint)ptyIndex);
    }

    /// <summary>
    ///     Allocates a new PTY pair and returns the master side.
    /// </summary>
    /// <returns>The newly allocated PTY pair, or null if allocation failed.</returns>
    public PtyPair? AllocatePty()
    {
        var index = Interlocked.Increment(ref _nextPtyIndex) - 1;
        if (index >= MAX_PTYS)
        {
            Interlocked.Decrement(ref _nextPtyIndex);
            _logger.LogWarning("[PtyManager] Maximum PTY count reached, cannot allocate more");
            return null;
        }

        var scheduler = _scheduler ?? throw new InvalidOperationException(
            "PtyManager must be bound to a KernelScheduler before allocating PTYs.");
        var pair = new PtyPair(index, this, _logger, scheduler);
        _ptyPairs[index] = pair;

        _logger.LogInformation("[PtyManager] Allocated PTY index={Index}", index);

        // Notify listeners (e.g., devpts) about the new PTY
        OnPtyCreated?.Invoke(index, pair);

        return pair;
    }

    /// <summary>
    ///     Releases a PTY pair.
    /// </summary>
    public void ReleasePty(int index)
    {
        if (_ptyPairs.TryRemove(index, out _))
        {
            _logger.LogInformation("[PtyManager] Released PTY index={Index}", index);
            OnPtyDestroyed?.Invoke(index);
        }
    }

    /// <summary>
    ///     Gets a PTY pair by index.
    /// </summary>
    public PtyPair? GetPty(int index)
    {
        return _ptyPairs.TryGetValue(index, out var pair) ? pair : null;
    }

    /// <summary>
    ///     Checks if a PTY with the given index exists.
    /// </summary>
    public bool PtyExists(int index)
    {
        return _ptyPairs.ContainsKey(index);
    }
}

/// <summary>
///     Represents a PTY pair (master and slave).
/// </summary>
public class PtyPair
{
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly PtyManager _manager;

    public PtyPair(int index, PtyManager manager, ILogger logger, KernelScheduler scheduler)
    {
        Index = index;
        _manager = manager;
        _logger = logger;

        Master = new PtyMaster(this, logger, scheduler);
        Slave = new PtySlave(this, logger);
    }

    /// <summary>
    ///     The index of this PTY (used as the minor device number for the slave).
    /// </summary>
    public int Index { get; }

    /// <summary>
    ///     The master side of the PTY.
    /// </summary>
    public PtyMaster Master { get; }

    /// <summary>
    ///     The slave side of the PTY.
    /// </summary>
    public PtySlave Slave { get; }

    /// <summary>
    ///     Whether the PTY is locked (unlocked by default for modern behavior).
    /// </summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    ///     Sets the locked state of the PTY.
    /// </summary>
    public void SetLocked(bool locked)
    {
        IsLocked = locked;
    }

    /// <summary>
    ///     Unlocks the PTY slave. Required before the slave can be opened.
    /// </summary>
    public void Unlock()
    {
        IsLocked = false;
        _logger.LogInformation("[PtyPair] PTY index={Index} unlocked", Index);
    }

    /// <summary>
    ///     Called when the master side is closed.
    /// </summary>
    public void CloseMaster()
    {
        _logger.LogInformation("[PtyPair] Master closed for PTY index={Index}", Index);
        _manager.ReleasePty(Index);
    }
}

/// <summary>
///     Buffer for PTY data transfer between master and slave.
/// </summary>
public class PtyBuffer
{
    private readonly Queue<byte> _buffer = new();
    private readonly object _lock = new();
    private readonly int _maxSize;

    public PtyBuffer(KernelScheduler scheduler, int maxSize = 65536)
    {
        _maxSize = maxSize;
        DataAvailable = new AsyncWaitQueue(scheduler);
    }

    public AsyncWaitQueue DataAvailable { get; }

    /// <summary>
    ///     Checks if data is available to read.
    /// </summary>
    public bool HasData
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count > 0;
            }
        }
    }

    /// <summary>
    ///     Gets the number of bytes available.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>
    ///     Writes data to the buffer.
    /// </summary>
    /// <returns>The number of bytes written, or a negative error code.</returns>
    public int Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var toWrite = Math.Min(data.Length, _maxSize - _buffer.Count);
            if (toWrite <= 0) return 0;

            for (var i = 0; i < toWrite; i++)
                _buffer.Enqueue(data[i]);

            DataAvailable.Signal();
            return toWrite;
        }
    }

    /// <summary>
    ///     Reads data from the buffer.
    /// </summary>
    /// <returns>The number of bytes read.</returns>
    public int Read(Span<byte> buffer)
    {
        lock (_lock)
        {
            var count = Math.Min(buffer.Length, _buffer.Count);
            for (var i = 0; i < count; i++)
                buffer[i] = _buffer.Dequeue();

            if (_buffer.Count > 0)
                DataAvailable.Signal();
            else
                DataAvailable.Reset();

            return count;
        }
    }
}
