using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Syscall handler threw before returning task. nr={Nr} tid={Tid} ret={Ret}")]
    private static partial void LogDispatchSyscallFailureCore(ILogger logger, Exception exception, uint nr, int tid,
        int ret);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Unimplemented Syscall: {Eax}")]
    private static partial void LogUnimplementedSyscallCore(ILogger logger, uint eax);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Syscall task completed with exception. nr={Nr} tid={Tid} ret={Ret}")]
    private static partial void LogCompletedSyscallFailureCore(ILogger logger, Exception exception, uint nr, int tid,
        int ret);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[Syscall] Async suspend TID={Tid} NR={Nr} IsCompleted={IsCompleted} AwaitRestart={AwaitRestart}")]
    private static partial void LogAsyncSyscallSuspendCore(ILogger logger, int tid, uint nr, bool isCompleted,
        bool awaitRestart);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = " [Suspended]")]
    private static partial void LogStraceSuspendedCore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Async syscall initiated but no FiberTask attached!")]
    private static partial void LogAsyncSyscallMissingFiberTaskCore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[RenameAt] olddirfd={OldDirFd} oldpath='{OldPath}' newdirfd={NewDirFd} newpath='{NewPath}'")]
    private static partial void LogRenameAtCore(ILogger logger, int oldDirFd, string oldPath, int newDirFd,
        string newPath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[RenameAt2] olddirfd={OldDirFd} oldpath='{OldPath}' newdirfd={NewDirFd} newpath='{NewPath}' flags={Flags}")]
    private static partial void LogRenameAt2Core(ILogger logger, int oldDirFd, string oldPath, int newDirFd,
        string newPath, uint flags);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Rename] PathWalkForCreate({PathKind}) failed err={Error}")]
    private static partial void LogRenamePathWalkFailureCore(ILogger logger, string pathKind, int error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[SysPrctl] Unhandled option: {Option}")]
    private static partial void LogUnhandledPrctlOptionCore(ILogger logger, int option);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[SysSetThreadArea] Entry={Entry} Base={BaseAddrHex}")]
    private static partial void LogSetThreadAreaCore(ILogger logger, uint entry, string baseAddrHex);

    private static void LogRenameAt(ReadOnlySpan<byte> oldPath, int oldDirFd, ReadOnlySpan<byte> newPath, int newDirFd)
    {
        if (!Logger.IsEnabled(LogLevel.Information))
            return;

        LogRenameAtCore(
            Logger,
            oldDirFd,
            FsEncoding.DecodeUtf8Lossy(oldPath),
            newDirFd,
            FsEncoding.DecodeUtf8Lossy(newPath));
    }

    private static void LogDispatchSyscallFailure(Exception exception, uint nr, int tid, int ret)
    {
        LogDispatchSyscallFailureCore(Logger, exception, nr, tid, ret);
    }

    private static void LogUnimplementedSyscall(uint eax)
    {
        LogUnimplementedSyscallCore(Logger, eax);
    }

    private static void LogCompletedSyscallFailure(Exception exception, uint nr, int tid, int ret)
    {
        LogCompletedSyscallFailureCore(Logger, exception, nr, tid, ret);
    }

    private static void LogAsyncSyscallSuspend(int tid, uint nr, bool isCompleted, bool awaitRestart)
    {
        LogAsyncSyscallSuspendCore(Logger, tid, nr, isCompleted, awaitRestart);
    }

    private static void LogStraceSuspended()
    {
        LogStraceSuspendedCore(Logger);
    }

    private static void LogAsyncSyscallMissingFiberTask()
    {
        LogAsyncSyscallMissingFiberTaskCore(Logger);
    }

    private static void LogRenameAt2(ReadOnlySpan<byte> oldPath, int oldDirFd, ReadOnlySpan<byte> newPath, int newDirFd,
        uint flags)
    {
        if (!Logger.IsEnabled(LogLevel.Information))
            return;

        LogRenameAt2Core(
            Logger,
            oldDirFd,
            FsEncoding.DecodeUtf8Lossy(oldPath),
            newDirFd,
            FsEncoding.DecodeUtf8Lossy(newPath),
            flags);
    }

    private static void LogRenamePathWalkFailure(string pathKind, int error)
    {
        LogRenamePathWalkFailureCore(Logger, pathKind, error);
    }

    private static void LogUnhandledPrctlOption(int option)
    {
        LogUnhandledPrctlOptionCore(Logger, option);
    }

    private static void LogSetThreadArea(uint entry, uint baseAddr)
    {
        if (!Logger.IsEnabled(LogLevel.Information))
            return;

        LogSetThreadAreaCore(Logger, entry, baseAddr.ToString("X"));
    }
}
