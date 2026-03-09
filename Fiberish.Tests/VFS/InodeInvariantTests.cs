using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class InodeInvariantTests
{
    [Fact]
    public void InodePut_Underflow_ThrowsInStrictMode()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            var sb = new TestSuperBlock();
            var inode = new TestInode(100, sb);

            var ex = Assert.Throws<InvalidOperationException>(() => inode.Put());
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

            dentry.Inode = null;
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
        }
    }

    [Fact]
    public void RefTrace_RecordsGetAndPut()
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

            inode.Get();
            inode.Put();

            var trace = VfsDebugTrace.SnapshotRefTrace().Where(t => t.Ino == inode.Ino).ToList();
            Assert.Contains(trace, t => t.Operation == "Inode.Get" && t.RefBefore == 0 && t.RefAfter == 1);
            Assert.Contains(trace, t => t.Operation == "Inode.Put" && t.RefBefore == 1 && t.RefAfter == 0);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
            VfsDebugTrace.ClearRefTrace();
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
