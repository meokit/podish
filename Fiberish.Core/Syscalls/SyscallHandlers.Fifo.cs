using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

internal enum FifoOpenCheckpoint { Reserved, Attached, BeforePublish }

public partial class SyscallManager
{
    internal Action<FifoOpenCheckpoint>? FifoOpenFaultForTesting { get; set; }

    private ValueTask<int> OpenNamedFifo(Dentry dentry, FileFlags flags, Mount mount, FiberTask task)
    {
        var table = GetTaskFdTable(task);
        var fd = 0;
        while (table.Fds.ContainsKey(fd) || table.IsReserved(fd)) fd++;
        if (task.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_NOFILE, out var limit) &&
            (ulong)fd >= limit.Soft)
            return ValueTask.FromResult(-(int)Errno.EMFILE);

        var inode = dentry.Inode!;
        if ((flags & FileFlags.O_ACCMODE) == FileFlags.O_WRONLY &&
            (flags & FileFlags.O_NONBLOCK) != 0 && inode.NamedPipe?.HasReaders != true)
            return ValueTask.FromResult(-(int)Errno.ENXIO);

        // Reserve without publishing a file: siblings must not fget/close an
        // incompletely opened FIFO, and concurrent opens cannot steal its fd.
        table.Reserve(fd);
        table.AddRef();
        LinuxFile? file = null;
        var transferred = false;
        try
        {
            FifoOpenFaultForTesting?.Invoke(FifoOpenCheckpoint.Reserved);
            file = new LinuxFile(dentry, flags, mount) { SuppressCloseNotification = true };
            var pipe = inode.NamedPipe ??= new PipeInode(task.CommonKernel, false, inode);
            pipe.Open(file);
            file.NamedPipe = pipe;
            FifoOpenFaultForTesting?.Invoke(FifoOpenCheckpoint.Attached);
            if (pipe.NeedsOpenPeer(file))
            {
                var pending = new PendingFifoOpen(table, task, file, fd);
                transferred = true;
                return CompleteFifoOpen(pending, pipe);
            }
            PublishFifo(table, file, fd);
            file = null;
            return ValueTask.FromResult(fd);
        }
        finally
        {
            if (!transferred)
            {
                file?.Close();
                inode.NamedPipe?.ReleaseIfUnopened();
                table.Unreserve(fd);
                ReleaseFdTable(table, task.Process);
            }
        }
    }

    private void PublishFifo(SharedFdTable table, LinuxFile file, int fd)
    {
        FifoOpenFaultForTesting?.Invoke(FifoOpenCheckpoint.BeforePublish);
        try
        {
            if ((file.Flags & FileFlags.O_CLOEXEC) != 0) table.CloseOnExec.Add(fd);
            table.Fds.Add(fd, file);
            InotifyEvents.RaiseChild(file.Dentry, file.OpenedInode, LinuxConstants.IN_OPEN);
        }
        catch
        {
            table.CloseOnExec.Remove(fd);
            table.Fds.Remove(fd);
            throw;
        }
        file.SuppressCloseNotification = false;
    }

    private async ValueTask<int> CompleteFifoOpen(PendingFifoOpen pending, PipeInode pipe)
    {
        var task = pending.Task;
        TaskAsyncOperationHandle? operation = null;
        try
        {
            // A retiring task does not resume its async state machine. Register
            // resource ownership separately so retirement still rolls back open.
            if (!task.TryEnterAsyncOperation(null, out operation) || operation == null)
                return -(int)Errno.ERESTARTSYS;
            operation.TryAddRegistration(pending);
            var rc = await pipe.WaitForOpenPeer(pending.File!, task);
            if (rc != 0) return rc;
            if (task.Exited || task.IsRetiring) return -(int)Errno.ERESTARTSYS;
            PublishFifo(pending.Table!, pending.File!, pending.Fd);
            pending.File = null; // descriptor table now owns the reference
            return pending.Fd;
        }
        catch (Exception ex)
        {
            return MapSyscallExceptionToErrno(ex);
        }
        finally
        {
            operation?.TryCompleteWithoutScheduling();
            pending.Dispose();
        }
    }

    private sealed class PendingFifoOpen(SharedFdTable table,
        FiberTask task, LinuxFile file, int fd) : ITaskAsyncRegistration
    {
        public SharedFdTable? Table { get; private set; } = table;
        public FiberTask Task { get; } = task;
        public LinuxFile? File { get; set; } = file;
        public int Fd { get; } = fd;
        public bool IsActive => Table != null;
        public void Cancel() => Dispose();

        public void Dispose()
        {
            var ownedTable = Table;
            if (ownedTable == null) return;
            Table = null;
            var ownedFile = File;
            File = null;
            ownedFile?.Close();
            ownedTable.Unreserve(Fd);
            ReleaseFdTable(ownedTable, Task.Process);
        }
    }
}
