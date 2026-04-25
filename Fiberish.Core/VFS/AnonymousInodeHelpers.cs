using Fiberish.Core;

namespace Fiberish.VFS;

internal sealed class PooledReadWriteWaitQueues
{
    private bool _returned;

    public PooledReadWriteWaitQueues(KernelScheduler scheduler)
    {
        Scheduler = scheduler;
        ReadQueue = scheduler.RentAsyncWaitQueue();
        WriteQueue = scheduler.RentAsyncWaitQueue();
    }

    public KernelScheduler Scheduler { get; }
    public AsyncWaitQueue ReadQueue { get; }
    public AsyncWaitQueue WriteQueue { get; }

    public void ReturnToPool()
    {
        if (_returned)
            return;

        Scheduler.ReturnAsyncWaitQueue(ReadQueue);
        Scheduler.ReturnAsyncWaitQueue(WriteQueue);
        _returned = true;
    }
}

internal static class AnonymousInodeLifecycle
{
    public static void Initialize(Inode inode, string reason)
    {
        inode.SetInitialLinkCount(1, reason);
    }

    public static void CloseAliasesAndFinalize(Inode inode, string reason)
    {
        var aliasCount = inode.Dentries.Count;
        var aliases = new Dentry[aliasCount];
        for (var i = 0; i < aliasCount; i++)
            aliases[i] = inode.Dentries[i];

        foreach (var alias in aliases)
        {
            if (!alias.UnbindInode(reason))
                continue;

            if (alias.DentryRefCount == 0 && alias.IsTrackedBySuperBlock)
                alias.UntrackFromSuperBlock(reason);
        }

        if (inode.LinkCount > 0)
            inode.DecLink(reason);
    }
}
