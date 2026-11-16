using Fiberish.Core;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // Linux fget semantics: descriptor removal must not release an in-flight
    // open description. Ready I/O needs only these stack-local reference pins;
    // allocate retirement cleanup only when an operation really suspends.
    private struct FileIoLease : IDisposable
    {
        private LinuxFile? _first;
        private LinuxFile? _second;

        public FileIoLease(LinuxFile first, LinuxFile? second = null)
        {
            _first = first;
            _second = ReferenceEquals(first, second) ? null : second;
            first.Get();
            _second?.Get();
        }

        public ValueTask<T> Complete<T>(ValueTask<T> operation, FiberTask? task)
        {
            if (operation.IsCompleted) return operation;
            var pending = new PendingFileIo(_first!, _second);
            _first = _second = null;
            try { return CompletePinnedFileIo(operation, task, pending); }
            catch
            {
                pending.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var first = _first;
            var second = _second;
            _first = _second = null;
            first?.Close();
            second?.Close();
        }
    }

    private static async ValueTask<T> CompletePinnedFileIo<T>(ValueTask<T> operation, FiberTask? task,
        PendingFileIo pending)
    {
        TaskAsyncOperationHandle? cleanup = null;
        try
        {
            if (task != null)
            {
                if (task.TryEnterAsyncOperation(null, out cleanup) && cleanup != null)
                    cleanup.TryAddRegistration(pending);
                else
                    pending.Dispose(); // Already retiring; its I/O continuation will not run.
            }
            return await operation;
        }
        finally
        {
            cleanup?.TryCompleteWithoutScheduling();
            pending.Dispose();
        }
    }

    private sealed class PendingFileIo(LinuxFile first, LinuxFile? second) : ITaskAsyncRegistration
    {
        private LinuxFile? _first = first;
        private LinuxFile? _second = second;
        public bool IsActive => _first != null;
        public void Cancel() => Dispose();
        public void Dispose()
        {
            Interlocked.Exchange(ref _first, null)?.Close();
            Interlocked.Exchange(ref _second, null)?.Close();
        }
    }
}
