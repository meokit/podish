using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class InodeInvariantTests
{
    [Fact]
    public void ReleaseRef_Underflow_ThrowsInStrictMode()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var inode = new TestInode(100, sb);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                inode.ReleaseRef(InodeRefKind.KernelInternal, "test"));
            Assert.Contains("underflow", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, inode.RefCount);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void AssertDentryMembership_ThrowsAfterDetachWithoutUnbind()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var parent = new Dentry("/", null, null, sb);
            parent.Parent = parent;
            sb.Root = parent;

            var inode = new TestInode(101, sb);
            var dentry = new Dentry("file", inode, parent, sb);

            Assert.Contains(dentry, inode.Dentries);
            Assert.True(inode.DetachAliasDentry(dentry, "InodeInvariantTests"));
            Assert.DoesNotContain(dentry, inode.Dentries);

            Assert.Throws<InvalidOperationException>(() =>
                VfsDebugTrace.AssertDentryMembership(dentry, "InodeInvariantTests"));

            dentry.UnbindInode("InodeInvariantTests.cleanup");
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void RefTrace_RecordsAcquireAndReleaseRef()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;
            VfsDebugTrace.ClearRefTrace();

            var sb = new TestSuperBlock();
            var inode = new TestInode(102, sb);

            inode.AcquireRef(InodeRefKind.KernelInternal, "test");
            inode.ReleaseRef(InodeRefKind.KernelInternal, "test");

            var trace = VfsDebugTrace.SnapshotRefTrace().Where(t => t.Ino == inode.Ino).ToList();
            Assert.Contains(trace,
                t => t.Operation == "Inode.AcquireRef.KernelInternal" && t.RefBefore == 0 && t.RefAfter == 1);
            Assert.Contains(trace,
                t => t.Operation == "Inode.ReleaseRef.KernelInternal" && t.RefBefore == 1 && t.RefAfter == 0);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
            VfsDebugTrace.ClearRefTrace();
        }
    }

    [Fact]
    public void DecLink_Underflow_ThrowsInStrictMode()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var inode = new TestInode(103, sb);
            inode.SetInitialLinkCount(0, "test");

            var ex = Assert.Throws<InvalidOperationException>(() => inode.DecLink("test"));
            Assert.Contains("underflow", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void UnbindInode_RemovesMembershipAndKernelInternalRef()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var parent = new Dentry("/", null, null, sb);
            parent.Parent = parent;
            sb.Root = parent;

            var inode = new TestInode(104, sb);
            var dentry = new Dentry("file", inode, parent, sb);
            Assert.Equal(1, inode.RefCount);
            Assert.Equal(1, inode.KernelInternalRefCount);
            Assert.Contains(dentry, inode.Dentries);

            Assert.True(dentry.UnbindInode("test"));
            Assert.Null(dentry.Inode);
            Assert.DoesNotContain(dentry, inode.Dentries);
            Assert.Equal(0, inode.RefCount);
            Assert.Equal(0, inode.KernelInternalRefCount);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void LinkCount_DefaultsByInodeType_WhenNotExplicit()
    {
        var sb = new TestSuperBlock();

        var fileInode = new TestInode(105, sb) { Type = InodeType.File };
        Assert.False(fileInode.HasExplicitLinkCount);
        Assert.Equal(1u, fileInode.GetLinkCountForStat());
        fileInode.IncLink("test-file");
        Assert.True(fileInode.HasExplicitLinkCount);
        Assert.Equal(2, fileInode.LinkCount);

        var dirInode = new TestInode(106, sb) { Type = InodeType.Directory };
        Assert.False(dirInode.HasExplicitLinkCount);
        Assert.Equal(2u, dirInode.GetLinkCountForStat());
        dirInode.DecLink("test-dir");
        Assert.True(dirInode.HasExplicitLinkCount);
        Assert.Equal(1, dirInode.LinkCount);
    }

    [Fact]
    public void ReleaseRef_ToZero_DoesNotEvictWithoutShrinker_WhenLinked()
    {
        var sb = new TestSuperBlock();
        var inode = new TestInode(108, sb);
        var root = new Dentry("/", null, null, sb);
        root.Parent = root;
        sb.Root = root;
        var alias = new Dentry("leaf", inode, root, sb);
        Assert.True(alias.UnbindInode("test-setup"));

        inode.SetInitialLinkCount(1, "test");
        inode.AcquireRef(InodeRefKind.FileOpen, "test");

        inode.ReleaseRef(InodeRefKind.FileOpen, "test");

        Assert.Equal(0, inode.RefCount);
        Assert.False(inode.IsCacheEvicted);
        Assert.False(inode.IsFinalized);

        var evicted = VfsShrinker.EvictUnusedInodes(sb);
        Assert.Equal(1, evicted);
        Assert.True(inode.IsCacheEvicted);
    }

    [Fact]
    public void FinalizeDelete_EvictsImmediately_WhenRefAndLinkDropToZero()
    {
        var sb = new TestSuperBlock();
        var inode = new TestInode(109, sb);
        inode.SetInitialLinkCount(1, "test");
        inode.AcquireRef(InodeRefKind.FileOpen, "test");

        inode.DecLink("test");
        Assert.False(inode.IsFinalized);

        inode.ReleaseRef(InodeRefKind.FileOpen, "test");
        Assert.True(inode.IsFinalized);
        Assert.True(inode.IsCacheEvicted);
    }

    [Fact]
    public void DentryPut_Underflow_ThrowsInStrictMode()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var parent = new Dentry("/", null, null, sb);
            parent.Parent = parent;
            sb.Root = parent;
            var dentry = new Dentry("leaf", null, parent, sb);

            var ex = Assert.Throws<InvalidOperationException>(() => dentry.Put("test"));
            Assert.Contains("underflow", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void DentryGetPut_TransitionsSuperblockLruMembership()
    {
        var sb = new TestSuperBlock();
        var root = new Dentry("/", null, null, sb);
        root.Parent = root;
        sb.Root = root;

        var dentry = new Dentry("leaf", null, root, sb);
        root.CacheChild(dentry, "test");

        Assert.True(dentry.IsTrackedBySuperBlock);
        Assert.True(dentry.IsHashed);
        Assert.True(dentry.IsNegative);
        Assert.True(dentry.IsOnLru);
        Assert.Contains(dentry, sb.SnapshotDentryLru());

        dentry.Get("test");
        Assert.Equal(1, dentry.DentryRefCount);
        Assert.False(dentry.IsOnLru);
        Assert.DoesNotContain(dentry, sb.SnapshotDentryLru());

        dentry.Put("test");
        Assert.Equal(0, dentry.DentryRefCount);
        Assert.True(dentry.IsHashed);
        Assert.True(dentry.IsOnLru);
        Assert.Contains(dentry, sb.SnapshotDentryLru());
    }

    [Fact]
    public void DropDentryCache_ReclaimsLeafAndUntracksDentry()
    {
        var sb = new TestSuperBlock();
        var root = new Dentry("/", null, null, sb);
        root.Parent = root;
        sb.Root = root;
        root.Get("test-root-pin");

        var inode = new TestInode(107, sb);
        var leaf = new Dentry("leaf", inode, root, sb);
        root.CacheChild(leaf, "test");
        Assert.True(leaf.IsHashed);
        Assert.False(leaf.IsNegative);

        var dropped = VfsShrinker.DropDentryCache(sb);

        Assert.Equal(1, dropped);
        Assert.False(root.TryGetCachedChild("leaf", out _));
        Assert.Null(leaf.Inode);
        Assert.False(leaf.IsHashed);
        Assert.True(leaf.IsNegative);
        Assert.False(leaf.IsTrackedBySuperBlock);
        Assert.DoesNotContain(leaf, sb.SnapshotDentryLru());
    }

    [Fact]
    public void TryUncacheChild_MountedChild_ThrowsInStrictMode()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var root = new Dentry("/", null, null, sb);
            root.Parent = root;
            sb.Root = root;

            var leaf = new Dentry("leaf", null, root, sb);
            root.CacheChild(leaf, "test");
            leaf.IsMounted = true;

            Assert.Throws<InvalidOperationException>(() =>
                root.TryUncacheChild("leaf", "test", out _));
            Assert.True(root.TryGetCachedChild("leaf", out var cached));
            Assert.Same(leaf, cached);
            Assert.True(leaf.IsHashed);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
        public TestSuperBlock()
        {
            Type = new FileSystemType
            {
                Name = "test",
                Factory = _ => throw new NotSupportedException("Not used in tests.")
            };
        }
    }

    private sealed class TestInode : Inode
    {
        public TestInode(ulong ino, SuperBlock sb)
        {
            Ino = ino;
            SuperBlock = sb;
            Type = InodeType.File;
            Mode = 0x1A4;
        }
    }
}
