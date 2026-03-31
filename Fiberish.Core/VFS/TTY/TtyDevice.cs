namespace Fiberish.Core.VFS.TTY;

/// <summary>
///     Thread-safe TTY hardware buffer that connects the background input thread
///     to the main scheduler thread. This maintains determinism by using a
///     producer-consumer pattern with interrupt flags.
///     Design Principles:
///     - Background thread (InputLoop) ONLY writes to this buffer
///     - Main thread (Scheduler) ONLY reads from this buffer
///     - No callbacks/events that would cross thread boundaries
///     - Interrupt flag allows efficient polling without dequeue operations
/// </summary>
public class TtyDevice
{
    public const int DefaultInputCapacityBytes = 64 * 1024;
    private readonly byte[] _hardwareBuffer;

    private readonly Lock _lock = new();
    private int _count;
    private int _head;

    // Volatile flag: indicates if new data is available
    // This avoids calling TryDequeue on every tick
    private volatile bool _interruptFlag;
    private int _pendingCols;
    private int _pendingRows;
    private volatile bool _resizePending;
    private int _tail;

    /// <summary>
    ///     Called by InputLoop (background thread) to enqueue input data.
    ///     This is the ONLY method called from the background thread.
    /// </summary>
    public TtyDevice(int inputCapacityBytes = DefaultInputCapacityBytes)
    {
        if (inputCapacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCapacityBytes));
        _hardwareBuffer = new byte[inputCapacityBytes];
    }

    /// <summary>
    ///     Check if there is pending input without consuming it.
    ///     Used by scheduler to decide whether to wake waiting tasks.
    /// </summary>
    public bool HasInterrupt => _interruptFlag || _resizePending;

    /// <summary>
    ///     True when the hardware input buffer contains unread bytes.
    ///     Unlike <see cref="HasInterrupt"/>, this excludes resize-only notifications.
    /// </summary>
    public bool HasBufferedInput => _interruptFlag;

    /// <summary>
    ///     Get the approximate number of items in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    // Event raised when input is enqueued (allows waking up sleeping scheduler)
    public event Action? OnInputEnqueued;

    public int EnqueueInput(byte[] data)
    {
        if (data.Length == 0) return 0;
        var enqueued = 0;
        lock (_lock)
        {
            var space = _hardwareBuffer.Length - _count;
            enqueued = Math.Min(data.Length, space);
            if (enqueued <= 0)
                return 0;

            var first = Math.Min(enqueued, _hardwareBuffer.Length - _tail);
            data.AsSpan(0, first).CopyTo(_hardwareBuffer.AsSpan(_tail, first));
            var second = enqueued - first;
            if (second > 0)
                data.AsSpan(first, second).CopyTo(_hardwareBuffer.AsSpan(0, second));

            _tail = (_tail + enqueued) % _hardwareBuffer.Length;
            _count += enqueued;
            _interruptFlag = true; // Raise interrupt line
        }

        OnInputEnqueued?.Invoke();
        return enqueued;
    }

    /// <summary>
    ///     Called by signal handler (background thread) to enqueue a resize event.
    /// </summary>
    public void EnqueueResize(int rows, int cols)
    {
        _pendingRows = rows;
        _pendingCols = cols;
        _resizePending = true;
        OnInputEnqueued?.Invoke();
    }

    /// <summary>
    ///     Called by KernelScheduler (main thread) to consume all pending input.
    ///     Returns null if no data is available.
    /// </summary>
    public List<byte[]>? ConsumeAll()
    {
        if (!_interruptFlag) return null;

        lock (_lock)
        {
            if (_count == 0)
            {
                _interruptFlag = false;
                return null;
            }

            var chunk = new byte[_count];
            var first = Math.Min(_count, _hardwareBuffer.Length - _head);
            _hardwareBuffer.AsSpan(_head, first).CopyTo(chunk.AsSpan(0, first));
            var second = _count - first;
            if (second > 0)
                _hardwareBuffer.AsSpan(0, second).CopyTo(chunk.AsSpan(first, second));

            _head = (_head + _count) % _hardwareBuffer.Length;
            _count = 0;
            _interruptFlag = false;
            return new List<byte[]> { chunk };
        }
    }

    /// <summary>
    ///     Called by KernelScheduler (main thread) to consume pending resize event.
    /// </summary>
    public (int Rows, int Cols)? ConsumeResize()
    {
        if (!_resizePending) return null;
        _resizePending = false;
        return (_pendingRows, _pendingCols);
    }
}
