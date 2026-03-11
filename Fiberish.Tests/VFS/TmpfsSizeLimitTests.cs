using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public sealed class TmpfsSizeLimitTests
{
    [Fact]
    public void ReadSuper_SizeOption_ParsesLimitBytes()
    {
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = (TmpfsSuperBlock)fsType.CreateFileSystem().ReadSuper(fsType, 0, "tmpfs-size-parse", "size=64k,nosuid");

        Assert.Equal(64 * 1024, sb.SizeLimitBytes);
    }

    [Fact]
    public void WriteBeyondSizeLimit_ReturnsEnospc_AndShrinkFreesSpace()
    {
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = (TmpfsSuperBlock)fsType.CreateFileSystem().ReadSuper(fsType, 0, "tmpfs-size-write", "size=4k");
        var root = sb.Root;
        var mount = new Mount(sb, root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw,size=4k" };

        var fileDentry = new Dentry("file", null, root, sb);
        root.Inode!.Create(fileDentry, 0x1A4, 0, 0);
        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, mount);

        var fill = new byte[LinuxConstants.PageSize];
        Assert.Equal(fill.Length, fileDentry.Inode!.Write(file, fill, 0));
        Assert.Equal(LinuxConstants.PageSize, sb.UsedDataBytes);
        Assert.Equal(-(int)Errno.ENOSPC, fileDentry.Inode!.Truncate(LinuxConstants.PageSize + 1));

        Assert.Equal(-(int)Errno.ENOSPC, fileDentry.Inode!.Write(file, new byte[] { 0x1 }, LinuxConstants.PageSize));

        Assert.Equal(0, fileDentry.Inode!.Truncate(2048));
        Assert.Equal(2048, sb.UsedDataBytes);

        var refill = new byte[2048];
        Assert.Equal(refill.Length, fileDentry.Inode!.Write(file, refill, 2048));
        Assert.Equal(LinuxConstants.PageSize, sb.UsedDataBytes);
        Assert.Equal(-(int)Errno.ENOSPC, fileDentry.Inode!.Write(file, new byte[] { 0x2 }, LinuxConstants.PageSize));
    }
}