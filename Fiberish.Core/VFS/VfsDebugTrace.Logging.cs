using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

public static partial class VfsDebugTrace
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-Ref] op={Operation} ino={Ino} type={Type} ref:{Before}->{After} dentries={DentryCount} detail={Detail}")]
    private static partial void LogRefChangeCore(ILogger logger, string operation, ulong ino, InodeType type, int before,
        int after, int dentryCount, string detail);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-Dentry] op={Operation} reason={Reason} ino={Ino} dentry={Name} dentryId={DentryId} parent={Parent} dentries={DentryCount}")]
    private static partial void LogDentryBindingCore(ILogger logger, string operation, string reason, ulong ino,
        string name, long dentryId, string parent, int dentryCount);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-DentryRef] op={Operation} dentry={Name} dentryId={DentryId} ref:{Before}->{After} lru={Lru} reason={Reason}")]
    private static partial void LogDentryRefChangeCore(ILogger logger, string operation, FsName name, long dentryId,
        int before, int after, bool lru, string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-Dcache] op={Operation} reason={Reason} parent={ParentName} parentId={ParentId} child={ChildName} childId={ChildId}")]
    private static partial void LogDentryCacheUpdateCore(ILogger logger, string operation, string reason,
        FsName parentName, long parentId, FsName childName, long childId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-StatNlink] source={Source} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} dentries={DentryCount}")]
    private static partial void LogStatNlinkCore(ILogger logger, string source, ulong ino, InodeType type, uint nlink,
        int refCount, int dentryCount);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-Link] op={Operation} ino={Ino} type={Type} nlink:{Before}->{After} ref={RefCount} dentries={DentryCount} reason={Reason}")]
    private static partial void LogLinkChangeCore(ILogger logger, string operation, ulong ino, InodeType type,
        int before, int after, int refCount, int dentryCount, string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-CacheEvict] op={Operation} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} reason={Reason}")]
    private static partial void LogCacheEvictCore(ILogger logger, string operation, ulong ino, InodeType type,
        int nlink, int refCount, string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[VFS-Finalize] op={Operation} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} reason={Reason}")]
    private static partial void LogFinalizeCore(ILogger logger, string operation, ulong ino, InodeType type,
        int nlink, int refCount, string reason);
}
