using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
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
