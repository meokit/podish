using System.Collections.Concurrent;

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
    // Thread-safe hardware buffer (background thread writes, kernel reads)
    private readonly ConcurrentQueue<byte[]> _hardwareBuffer = new();

    // Volatile flag: indicates if new data is available
    // This avoids calling TryDequeue on every tick
    private volatile bool _interruptFlag;

    /// <summary>
    ///     Check if there is pending input without consuming it.
    ///     Used by scheduler to decide whether to wake waiting tasks.
    /// </summary>
    public bool HasInterrupt => _interruptFlag;

    /// <summary>
    ///     Get the approximate number of items in the buffer.
    /// </summary>
    public int Count => _hardwareBuffer.Count;

    // Event raised when input is enqueued (allows waking up sleeping scheduler)
    public event Action? OnInputEnqueued;

    /// <summary>
    ///     Called by InputLoop (background thread) to enqueue input data.
    ///     This is the ONLY method called from the background thread.
    /// </summary>
    public void EnqueueInput(byte[] data)
    {
        if (data.Length == 0) return;
        _hardwareBuffer.Enqueue(data);
        _interruptFlag = true; // Raise interrupt line
        OnInputEnqueued?.Invoke();
    }

    /// <summary>
    ///     Called by KernelScheduler (main thread) to consume all pending input.
    ///     Returns null if no data is available.
    /// </summary>
    public List<byte[]>? ConsumeAll()
    {
        if (!_interruptFlag) return null;

        var result = new List<byte[]>();
        while (_hardwareBuffer.TryDequeue(out var data)) result.Add(data);

        // Clear interrupt flag after consuming all data
        _interruptFlag = false;

        return result.Count > 0 ? result : null;
    }
}