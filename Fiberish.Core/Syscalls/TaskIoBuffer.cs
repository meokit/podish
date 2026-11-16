using System.Buffers;
using Fiberish.Core;

namespace Fiberish.Syscalls;

// A rented buffer normally lives in an async finally. Guest retirement drops
// continuations, so transfer its cleanup to the task scope only on suspension.
// The ready path remains stack/state-machine local and allocation-free.
internal struct TaskIoBuffer<T>(T[] buffer, ArrayPool<T>? pool = null) : IDisposable
{
    private T[]? _buffer = buffer;
    private readonly ArrayPool<T> _pool = pool ?? ArrayPool<T>.Shared;
    private PendingBuffer? _pending;
    private TaskAsyncOperationHandle? _cleanup;

    internal ValueTask<TResult> Track<TResult>(ValueTask<TResult> operation, FiberTask? task)
    {
        if (!operation.IsCompleted) TrackPending(task);
        return operation;
    }

    internal void TrackPending(FiberTask? task)
    {
        if (_pending != null || task == null) return;
        _pending = new PendingBuffer(_buffer!, _pool);
        if (task.TryEnterAsyncOperation(null, out _cleanup) && _cleanup != null)
            _cleanup.TryAddRegistration(_pending);
        else
            _pending.Dispose();
    }

    public void Dispose()
    {
        if (_buffer == null) return;
        var array = _buffer;
        _buffer = null;
        _cleanup?.TryCompleteWithoutScheduling();
        if (_pending != null) _pending.Dispose();
        else _pool.Return(array);
    }

    private sealed class PendingBuffer(T[] buffer, ArrayPool<T> pool) : ITaskAsyncRegistration
    {
        private T[]? _buffer = buffer;
        public bool IsActive => _buffer != null;
        public void Cancel() => Dispose();
        public void Dispose()
        {
            var array = Interlocked.Exchange(ref _buffer, null);
            if (array != null) pool.Return(array);
        }
    }
}
