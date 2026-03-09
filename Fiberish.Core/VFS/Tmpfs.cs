namespace Fiberish.VFS;

public class Tmpfs : FileSystem
{
    public Tmpfs(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "tmpfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var sb = new TmpfsSuperBlock(fsType, DevManager);
        var rootInode = sb.AllocInode();
        rootInode.Type = InodeType.Directory;
        rootInode.Mode = 0x1FF;
        rootInode.SetInitialLinkCount(1, "Tmpfs.ReadSuper.root");

        sb.Root = new Dentry("/", rootInode, null, sb);
        sb.Root.Parent = sb.Root;

        return sb;
    }
}

public class TmpfsSuperBlock : IndexedMemorySuperBlock
{
    public TmpfsSuperBlock(FileSystemType type, DeviceNumberManager devManager) : base(type, devManager)
    {
    }

    protected override IndexedMemoryInode CreateIndexedInode(ulong ino)
    {
        return new TmpfsInode(ino, this);
    }
}

public class TmpfsInode : IndexedMemoryInode
{
    public TmpfsInode(ulong ino, SuperBlock sb) : base(ino, (IndexedMemorySuperBlock)sb)
    {
    }
}
