using Fiberish.VFS;

namespace Fiberish.Tests.VFS;

public interface IFileSystemTestRig : IDisposable
{
    string Name { get; }
    SuperBlock SuperBlock { get; }
    Dentry Root { get; }
    Inode RootInode { get; }
}

public static class FileSystemTestRigFactory
{
    public static IFileSystemTestRig Create(string name)
    {
        return name switch
        {
            "hostfs" => new HostfsTestRig(),
            "tmpfs" => new TmpfsTestRig(),
            "overlayfs" => new OverlayFsTestRig(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown filesystem rig")
        };
    }
}

internal sealed class HostfsTestRig : IFileSystemTestRig
{
    private readonly string _rootPath;

    public HostfsTestRig()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "hostfs-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);

        var fsType = new FileSystemType { Name = "hostfs" };
        var opts = HostfsMountOptions.Parse("rw");
        SuperBlock = new HostSuperBlock(fsType, _rootPath, opts);
        SuperBlock.Root = ((HostSuperBlock)SuperBlock).GetDentry(_rootPath, "/", null)!;
        Root = SuperBlock.Root;
        RootInode = Root.Inode!;
    }

    public string Name => "hostfs";
    public SuperBlock SuperBlock { get; }
    public Dentry Root { get; }
    public Inode RootInode { get; }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, true);
    }
}

internal sealed class TmpfsTestRig : IFileSystemTestRig
{
    public TmpfsTestRig()
    {
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        SuperBlock = fsType.CreateAnonymousFileSystem().ReadSuper(fsType, 0, "tmpfs-rig", null);
        Root = SuperBlock.Root;
        RootInode = Root.Inode!;
    }

    public string Name => "tmpfs";
    public SuperBlock SuperBlock { get; }
    public Dentry Root { get; }
    public Inode RootInode { get; }

    public void Dispose()
    {
    }
}

internal sealed class OverlayFsTestRig : IFileSystemTestRig
{
    public OverlayFsTestRig()
    {
        var lowerType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lower = lowerType.CreateAnonymousFileSystem().ReadSuper(lowerType, 0, "ovl-lower", null);
        var upper = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        SuperBlock = overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay-rig",
            new OverlayMountOptions { Lower = lower, Upper = upper });
        Root = SuperBlock.Root;
        RootInode = Root.Inode!;
    }

    public string Name => "overlayfs";
    public SuperBlock SuperBlock { get; }
    public Dentry Root { get; }
    public Inode RootInode { get; }

    public void Dispose()
    {
    }
}