using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsPolicyTests
{
    [Fact(Timeout = 1000)]
    public void Hostfs_CustomSpecialNodePolicy_CanHideRegularFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "data");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = new HostfsMountOptions { SpecialNodePolicy = new DirectoriesOnlySpecialNodePolicy() };
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var root = Assert.IsType<HostInode>(sb.Root.Inode);

            Assert.Null(root.Lookup("a.txt"));
            Assert.Equal(-(int)Errno.EOPNOTSUPP, root.ConsumeLookupFailureError("a.txt"));
            var names = root.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.DoesNotContain("a.txt", names);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact(Timeout = 1000)]
    public void Hostfs_CustomMountBoundaryPolicy_CanBlockDescendants()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "data");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = new HostfsMountOptions { MountBoundaryPolicy = new RootOnlyMountBoundaryPolicy() };
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var root = Assert.IsType<HostInode>(sb.Root.Inode);

            Assert.Null(root.Lookup("a.txt"));
            Assert.Equal(-(int)Errno.EXDEV, root.ConsumeLookupFailureError("a.txt"));
            var names = root.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.DoesNotContain("a.txt", names);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact(Timeout = 1000)]
    public void Hostfs_PathWalkCreate_PropagatesPolicyError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "data");

        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = new HostfsMountOptions { MountBoundaryPolicy = new RootOnlyMountBoundaryPolicy() };
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            sb.Root.Parent = sb.Root;
            var mount = new Mount(sb, sb.Root) { Source = tempRoot, FsType = "hostfs", Options = "rw" };
            sm.InitializeRoot(sb.Root, mount);

            var nd = sm.PathWalkWithData("/a.txt", LookupFlags.Create | LookupFlags.FollowSymlink);
            Assert.True(nd.HasError);
            Assert.Equal(-(int)Errno.EXDEV, nd.ErrorCode);
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private sealed class DirectoriesOnlySpecialNodePolicy : ISpecialNodePolicy
    {
        public bool TryMapType(InodeType rawType, out InodeType type)
        {
            if (rawType == InodeType.Directory)
            {
                type = InodeType.Directory;
                return true;
            }

            type = InodeType.Unknown;
            return false;
        }
    }

    private sealed class RootOnlyMountBoundaryPolicy : IMountBoundaryPolicy
    {
        public bool Allows(string mountRootPath, string candidatePath, ulong? rootMountDomainId,
            ulong? candidateMountDomainId)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(mountRootPath),
                Path.GetFullPath(candidatePath),
                comparison);
        }
    }
}