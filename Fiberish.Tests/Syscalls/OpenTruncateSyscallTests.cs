using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class OpenTruncateSyscallTests
{
    [Fact]
    public async Task OpenWithTrunc_FollowedByShortWrite_MustDropOldTail()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000); // path
        env.MapUserPage(0x11000); // first payload
        env.MapUserPage(0x12000); // second payload
        env.MapUserPage(0x13000); // read buffer
        env.WriteCString(0x10000, "/trunc-open");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        env.WriteBytes(0x11000, "AAAAAA"u8.ToArray());
        Assert.Equal(6, await env.Call("SysWrite", (uint)fd, 0x11000, 6));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x10000, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        env.WriteBytes(0x12000, "B"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x12000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x10000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x13000, 16));
        Assert.Equal("B", Encoding.ASCII.GetString(env.ReadBytes(0x13000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task Creat_OnExistingFile_WithShortWrite_MustDropOldTail()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x20000); // path
        env.MapUserPage(0x21000); // first payload
        env.MapUserPage(0x22000); // second payload
        env.MapUserPage(0x23000); // read buffer
        env.WriteCString(0x20000, "/trunc-creat");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x20000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x20000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        env.WriteBytes(0x21000, "AAAAAA"u8.ToArray());
        Assert.Equal(6, await env.Call("SysWrite", (uint)fd, 0x21000, 6));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysCreat", 0x20000, 0x1A4);
        Assert.True(fd >= 0);
        env.WriteBytes(0x22000, "B"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x22000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x20000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x23000, 16));
        Assert.Equal("B", Encoding.ASCII.GetString(env.ReadBytes(0x23000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatTrunc_OnExistingLowerFile_MustDropOldTail()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("trunc-open", "AAAAAA"));
        env.MapUserPage(0x24000); // path
        env.MapUserPage(0x25000); // payload
        env.MapUserPage(0x26000); // read buffer
        env.WriteCString(0x24000, "/trunc-open");

        var fd = await env.Call("SysOpen", 0x24000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        env.WriteBytes(0x25000, "B"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x25000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x24000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x26000, 16));
        Assert.Equal("B", Encoding.ASCII.GetString(env.ReadBytes(0x26000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithTruncReadOnly_OnExistingLowerFile_MustStillDropOldTail()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("trunc-ro", "AAAAAA"));
        env.MapUserPage(0x27000); // path
        env.MapUserPage(0x28000); // read buffer
        env.WriteCString(0x27000, "/trunc-ro");

        var fd = await env.Call("SysOpen", 0x27000, (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x28000, 16));
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithTruncReadWrite_OnExistingLowerFile_MustRewriteFromZeroLength()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("trunc-rw", "AAAAAA"));
        env.MapUserPage(0x29000); // path
        env.MapUserPage(0x2A000); // payload
        env.MapUserPage(0x2B000); // read buffer
        env.WriteCString(0x29000, "/trunc-rw");

        var fd = await env.Call("SysOpen", 0x29000, (uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        env.WriteBytes(0x2A000, "Q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x2A000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x29000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x2B000, 16));
        Assert.Equal("Q", Encoding.ASCII.GetString(env.ReadBytes(0x2B000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithAppendTrunc_OnExistingLowerFile_MustAppendToEmptyFile()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("trunc-append", "AAAAAA"));
        env.MapUserPage(0x2F000); // path
        env.MapUserPage(0x30000); // payload
        env.MapUserPage(0x31000); // read buffer
        env.WriteCString(0x2F000, "/trunc-append");

        var fd = await env.Call("SysOpen", 0x2F000, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x30000, "P"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x30000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x2F000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x31000, 16));
        Assert.Equal("P", Encoding.ASCII.GetString(env.ReadBytes(0x31000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatTruncReadOnly_OnExistingLowerFile_MustStillDropOldTail()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("trunc-creat-ro", "AAAAAA"));
        env.MapUserPage(0x32000); // path
        env.MapUserPage(0x33000); // read buffer
        env.WriteCString(0x32000, "/trunc-creat-ro");

        var fd = await env.Call("SysOpen", 0x32000, (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x33000, 16));
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithTrunc_OnMissingFile_WithoutCreate_MustReturnEnoent()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("present", "AAAAAA"));
        env.MapUserPage(0x2C000); // path
        env.WriteCString(0x2C000, "/missing");

        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x2C000, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC)));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x2C000, (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC)));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x2C000, (uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC)));
    }

    [Fact]
    public async Task OverlayTruncate_OnExistingLowerFile_MustExposeExactNewSize()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("truncate-call", ":xxx:yyy:zzz"));
        env.MapUserPage(0x2D000); // path
        env.MapUserPage(0x2E000); // read buffer
        env.WriteCString(0x2D000, "/truncate-call");

        Assert.Equal(0, await env.Call("SysTruncate", 0x2D000, 5));

        var fd = await env.Call("SysOpen", 0x2D000);
        Assert.True(fd >= 0);
        Assert.Equal(5, await env.Call("SysRead", (uint)fd, 0x2E000, 16));
        Assert.Equal(":xxx:", Encoding.ASCII.GetString(env.ReadBytes(0x2E000, 5)));
        Assert.Equal(5, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreat_OnExistingLowerFile_MustNotTruncateAndMustOverwriteFromStart()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("creat-existing", ":xxx:yyy:zzz"));
        env.MapUserPage(0x34000); // path
        env.MapUserPage(0x35000); // payload
        env.MapUserPage(0x36000); // read buffer
        env.WriteCString(0x34000, "/creat-existing");

        var fd = await env.Call("SysOpen", 0x34000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        env.WriteBytes(0x35000, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x35000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x34000);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x36000, 16));
        Assert.Equal("qxxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x36000, 12)));
        Assert.Equal(12, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatAppend_OnExistingLowerFile_MustAppendWithoutTruncation()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("creat-append-existing", ":xxx:yyy:zzz"));
        env.MapUserPage(0x37000); // path
        env.MapUserPage(0x38000); // payload
        env.MapUserPage(0x39000); // read buffer
        env.WriteCString(0x37000, "/creat-append-existing");

        var fd = await env.Call("SysOpen", 0x37000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x38000, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x38000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x37000);
        Assert.True(fd >= 0);
        Assert.Equal(13, await env.Call("SysRead", (uint)fd, 0x39000, 16));
        Assert.Equal(":xxx:yyy:zzzq", Encoding.ASCII.GetString(env.ReadBytes(0x39000, 13)));
        Assert.Equal(13, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatExcl_OnExistingLowerFile_MustReturnEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("creat-excl-existing", ":xxx:yyy:zzz"));
        env.MapUserPage(0x3A000); // path
        env.MapUserPage(0x3B000); // read buffer
        env.WriteCString(0x3A000, "/creat-excl-existing");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x3A000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));

        var fd = await env.Call("SysOpen", 0x3A000);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x3B000, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x3B000, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreat_OnMissingFileReadOnly_MustCreateEmptyFile()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("present", ":xxx:yyy:zzz"));
        env.MapUserPage(0x3C000); // path
        env.MapUserPage(0x3D000); // read buffer
        env.WriteCString(0x3C000, "/new-ro");

        var fd = await env.Call("SysOpen", 0x3C000, (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x3D000, 16));
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x3C000);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x3D000, 16));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatAppend_OnMissingFile_MustCreateThenAppend()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("present", ":xxx:yyy:zzz"));
        env.MapUserPage(0x3E000); // path
        env.MapUserPage(0x3F000); // payload
        env.MapUserPage(0x40000); // read buffer
        env.WriteCString(0x3E000, "/new-append");

        var fd = await env.Call("SysOpen", 0x3E000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x3F000, "qp"u8.ToArray());
        Assert.Equal(2, await env.Call("SysWrite", (uint)fd, 0x3F000, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x3E000);
        Assert.True(fd >= 0);
        Assert.Equal(2, await env.Call("SysRead", (uint)fd, 0x40000, 16));
        Assert.Equal("qp", Encoding.ASCII.GetString(env.ReadBytes(0x40000, 2)));
        Assert.Equal(2, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatExcl_OnMissingFile_MustCreateOnceThenReturnEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("present", ":xxx:yyy:zzz"));
        env.MapUserPage(0x41000); // path
        env.MapUserPage(0x42000); // payload
        env.MapUserPage(0x43000); // read buffer
        env.WriteCString(0x41000, "/new-excl");

        var fd = await env.Call("SysOpen", 0x41000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL));
        Assert.True(fd >= 0);
        env.WriteBytes(0x42000, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x42000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x41000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));

        fd = await env.Call("SysOpen", 0x41000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x43000, 16));
        Assert.Equal("q", Encoding.ASCII.GetString(env.ReadBytes(0x43000, 1)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatExclTrunc_OnExistingLowerFile_MustReturnEexistWithoutTruncating()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("creat-excl-trunc-existing", ":xxx:yyy:zzz"));
        env.MapUserPage(0x44000); // path
        env.MapUserPage(0x45000); // read buffer
        env.WriteCString(0x44000, "/creat-excl-trunc-existing");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x44000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL |
                                                        FileFlags.O_TRUNC)));

        var fd = await env.Call("SysOpen", 0x44000);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x45000, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x45000, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenWithCreatExclTrunc_OnMissingFile_MustCreateThenReturnEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerFile("present", ":xxx:yyy:zzz"));
        env.MapUserPage(0x46000); // path
        env.MapUserPage(0x47000); // payload
        env.MapUserPage(0x48000); // read buffer
        env.WriteCString(0x46000, "/new-excl-trunc");

        var fd = await env.Call("SysOpen", 0x46000,
            (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        env.WriteBytes(0x47000, "z"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x47000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x46000,
                (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC)));

        fd = await env.Call("SysOpen", 0x46000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x48000, 16));
        Assert.Equal("z", Encoding.ASCII.GetString(env.ReadBytes(0x48000, 1)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatReadOnly_ReturnsTargetContent()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x49000u);
        env.MapUserPage(0x4A000u);
        env.WriteCString(0x49000u, "/link");

        var fd = await env.Call("SysOpen", 0x49000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x4A000u, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x4A000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatWrite_OverwritesTargetPrefix()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x4B000u);
        env.MapUserPage(0x4C000u);
        env.MapUserPage(0x4D000u);
        env.WriteCString(0x4B000u, "/link");

        var fd = await env.Call("SysOpen", 0x4B000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        env.WriteBytes(0x4C000u, "Q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x4C000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x4B000u);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x4D000u, 16));
        Assert.Equal("Qxxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x4D000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatAppend_AppendsAndPreservesContent()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x4E000u);
        env.MapUserPage(0x4F000u);
        env.MapUserPage(0x50000u);
        env.WriteCString(0x4E000u, "/link");

        var fd = await env.Call("SysOpen", 0x4E000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x4F000u, "Q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x4F000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x4E000u);
        Assert.True(fd >= 0);
        Assert.Equal(13, await env.Call("SysRead", (uint)fd, 0x50000u, 16));
        Assert.Equal(":xxx:yyy:zzzQ", Encoding.ASCII.GetString(env.ReadBytes(0x50000u, 13)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatReadWrite_CanOverwriteAndRead()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x51000u);
        env.MapUserPage(0x52000u);
        env.MapUserPage(0x53000);
        env.WriteCString(0x51000u, "/link");

        var fd = await env.Call("SysOpen", 0x51000u, (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        env.WriteBytes(0x52000u, "Z"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x52000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x51000u);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x53000, 16));
        Assert.Equal("Zxxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x53000, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatAppendReadWrite_AppendsFromSymlink()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x54000u);
        env.MapUserPage(0x55000u);
        env.MapUserPage(0x56000u);
        env.WriteCString(0x54000u, "/link");

        var fd = await env.Call("SysOpen", 0x54000u, (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x55000u, "R"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x55000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x54000u);
        Assert.True(fd >= 0);
        Assert.Equal(13, await env.Call("SysRead", (uint)fd, 0x56000u, 16));
        Assert.Equal(":xxx:yyy:zzzR", Encoding.ASCII.GetString(env.ReadBytes(0x56000u, 13)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatExclReadOnly_ReturnsEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x57000u);
        env.MapUserPage(0x58000u);
        env.WriteCString(0x57000u, "/link");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x57000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));

        var fd = await env.Call("SysOpen", 0x57000u);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x58000u, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x58000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatExclWriteOnly_ReturnsEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x59000u);
        env.MapUserPage(0x5A000u);
        env.WriteCString(0x59000u, "/link");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x59000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));

        var fd = await env.Call("SysOpen", 0x59000u);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x5A000u, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x5A000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenSymlinkWithCreatExclAppend_ReturnsEexist()
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x5B000u);
        env.MapUserPage(0x5C000u);
        env.WriteCString(0x5B000u, "/link");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen",
                0x5B000u,
                (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND)));

        var fd = await env.Call("SysOpen", 0x5B000u);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x5C000u, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x5C000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData((uint)FileFlags.O_RDONLY, "", ":xxx:yyy:zzz")]
    [InlineData((uint)FileFlags.O_WRONLY, "p", "pxxx:yyy:zzz")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq")]
    [InlineData((uint)FileFlags.O_RDWR, "z", "zxxx:yyy:zzz")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), "r", ":xxx:yyy:zzzr")]
    public async Task OverlayOpenDirectSymlink_PlainModes_MatchTargetSemantics(uint flags, string payload,
        string expected)
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x5D000u);
        env.MapUserPage(0x5E000u);
        env.MapUserPage(0x5F000u);
        env.WriteCString(0x5D000u, "/link");

        if ((flags & 3u) != (uint)FileFlags.O_RDONLY || (flags & (uint)FileFlags.O_APPEND) != 0)
        {
            var fd = await env.Call("SysOpen", 0x5D000u, flags);
            Assert.True(fd >= 0);
            env.WriteBytes(0x5E000u, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x5E000u, (uint)payload.Length));
            Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        }

        var readFd = await env.Call("SysOpen", 0x5D000u, (uint)FileFlags.O_RDONLY);
        Assert.True(readFd >= 0);
        Assert.Equal(expected.Length, await env.Call("SysRead", (uint)readFd, 0x5F000u, 32));
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x5F000u, expected.Length)));
        Assert.Equal(0, await env.Call("SysClose", (uint)readFd));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), "", "")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), "q", "q")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC), "p", "p")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC), "x", "x")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC), "y", "y")]
    public async Task OverlayOpenDirectSymlink_TruncModes_MatchTargetSemantics(uint flags, string payload,
        string expected)
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x60000u);
        env.MapUserPage(0x61000u);
        env.MapUserPage(0x62000u);
        env.WriteCString(0x60000u, "/link");

        var fd = await env.Call("SysOpen", 0x60000u, flags);
        Assert.True(fd >= 0);
        if (payload.Length > 0)
        {
            env.WriteBytes(0x61000u, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x61000u, (uint)payload.Length));
        }

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        var readFd = await env.Call("SysOpen", 0x60000u, (uint)FileFlags.O_RDONLY);
        Assert.True(readFd >= 0);
        Assert.Equal(expected.Length, await env.Call("SysRead", (uint)readFd, 0x62000u, 32));
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x62000u, expected.Length)));
        Assert.Equal(0, await env.Call("SysClose", (uint)readFd));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND))]
    public async Task OverlayOpenDirectSymlink_CreatExclRemainingModes_ReturnEexist(uint flags)
    {
        using var env = new TestEnv(CreateOverlayRootWithLowerSymlink("target", "link", ":xxx:yyy:zzz"));
        env.MapUserPage(0x63000u);
        env.MapUserPage(0x64000u);
        env.WriteCString(0x63000u, "/link");

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysOpen", 0x63000u, flags));

        var fd = await env.Call("SysOpen", 0x63000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(12, await env.Call("SysRead", (uint)fd, 0x64000u, 16));
        Assert.Equal(":xxx:yyy:zzz", Encoding.ASCII.GetString(env.ReadBytes(0x64000u, 12)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkPlain_EnoentPaths()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-plain", "/symx-plain-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x49000u); // symlink path
        env.MapUserPage(0x4A000u); // target path
        env.WriteCString(0x49000u, "/symx-plain");
        env.WriteCString(0x4A000u, "/symx-plain-target");

        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x49000u, (uint)FileFlags.O_RDONLY));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x4A000u, (uint)FileFlags.O_RDONLY));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND))]
    [InlineData((uint)FileFlags.O_RDWR)]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND))]
    public async Task OverlayOpenBrokenSymlink_PlainRemainingModes_ReturnEnoent(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-plain-more", "/symx-plain-more-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x65000u);
        env.WriteCString(0x65000u, "/symx-plain-more");

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x65000u, flags));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkTrunc_Enoent()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-trunc", "/symx-trunc-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x4B000u);
        env.WriteCString(0x4B000u, "/symx-trunc");

        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x4B000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC)));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", 0x4B000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC)));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC))]
    public async Task OverlayOpenBrokenSymlink_TruncRemainingModes_ReturnEnoent(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-trunc-more", "/symx-trunc-more-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x66000u);
        env.WriteCString(0x66000u, "/symx-trunc-more");

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x66000u, flags));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkCreat_ReadOnlyCreatesEmptyFile()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat", "/symx-creat-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x4C000u); // symlink path
        env.MapUserPage(0x4D000u); // target path
        env.MapUserPage(0x4E000u); // read buffer
        env.WriteCString(0x4C000u, "/symx-creat");
        env.WriteCString(0x4D000u, "/symx-creat-target");

        var fd = await env.Call("SysOpen", 0x4C000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x4E000u, 16));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x4D000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysRead", (uint)fd, 0x4E000u, 16));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkCreat_WriteOverwrites()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-write", "/symx-creat-write-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x4F000u);
        env.MapUserPage(0x50000u);
        env.MapUserPage(0x51000u);
        env.MapUserPage(0x52000u);
        env.WriteCString(0x4F000u, "/symx-creat-write");
        env.WriteCString(0x50000u, "/symx-creat-write-target");

        var fd = await env.Call("SysOpen", 0x4F000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        env.WriteBytes(0x51000u, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x51000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x50000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x52000u, 16));
        Assert.Equal("q", Encoding.ASCII.GetString(env.ReadBytes(0x52000u, 1)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x4F000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        env.WriteBytes(0x51000u, "p"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x51000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x50000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x52000u, 16));
        Assert.Equal("p", Encoding.ASCII.GetString(env.ReadBytes(0x52000u, 1)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkCreatAppend_AppendsData()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-append", "/symx-creat-append-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x53000);
        env.MapUserPage(0x54000u);
        env.MapUserPage(0x55000u);
        env.MapUserPage(0x56000u);
        env.WriteCString(0x53000, "/symx-creat-append");
        env.WriteCString(0x54000u, "/symx-creat-append-target");

        var fd = await env.Call("SysOpen", 0x53000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x55000u, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x55000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x53000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND));
        Assert.True(fd >= 0);
        env.WriteBytes(0x55000u, "p"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x55000u, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x53000, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(2, await env.Call("SysRead", (uint)fd, 0x56000u, 16));
        Assert.Equal("qp", Encoding.ASCII.GetString(env.ReadBytes(0x56000u, 2)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT), "m")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_APPEND), "n")]
    public async Task OverlayOpenBrokenSymlink_CreatRemainingModes_CreateTarget(uint flags, string payload)
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-more", "/symx-creat-more-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x67000u);
        env.MapUserPage(0x68000u);
        env.MapUserPage(0x69000u);
        env.WriteCString(0x67000u, "/symx-creat-more");
        env.WriteCString(0x68000u, "/symx-creat-more-target");

        var fd = await env.Call("SysOpen", 0x67000u, flags);
        Assert.True(fd >= 0);
        env.WriteBytes(0x69000u, Encoding.ASCII.GetBytes(payload));
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x69000u, (uint)payload.Length));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        var readFd = await env.Call("SysOpen", 0x68000u, (uint)FileFlags.O_RDONLY);
        Assert.True(readFd >= 0);
        Assert.Equal(payload.Length, await env.Call("SysRead", (uint)readFd, 0x69000u, 16));
        Assert.Equal(payload, Encoding.ASCII.GetString(env.ReadBytes(0x69000u, payload.Length)));
        Assert.Equal(0, await env.Call("SysClose", (uint)readFd));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkCreatExcl_ReturnsEexist()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-excl", "/symx-creat-excl-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x57000u);
        env.WriteCString(0x57000u, "/symx-creat-excl");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x57000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND))]
    public async Task OverlayOpenBrokenSymlink_CreatExclRemainingModes_ReturnEexist(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-excl-more", "/symx-creat-excl-more-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x6A000u);
        env.WriteCString(0x6A000u, "/symx-creat-excl-more");

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysOpen", 0x6A000u, flags));
    }

    [Fact]
    public async Task OverlayOpenBrokenSymlinkCreatExclTrunc_SucceedsThenEexist()
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-excl-trunc", "/symx-creat-excl-trunc-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x5A000u);
        env.WriteCString(0x5A000u, "/symx-creat-excl-trunc");

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x5A000u,
                (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC)));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), "")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), "q")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND | FileFlags.O_TRUNC), "p")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_TRUNC), "x")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_APPEND | FileFlags.O_TRUNC), "y")]
    public async Task OverlayOpenBrokenSymlink_CreatTruncModes_CreateTarget(uint flags, string payload)
    {
        var (root, mount) = CreateOverlayRootWithBrokenSymlink("symx-creat-trunc-more", "/symx-creat-trunc-more-target");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x6B000u);
        env.MapUserPage(0x6C000u);
        env.MapUserPage(0x6D000u);
        env.WriteCString(0x6B000u, "/symx-creat-trunc-more");
        env.WriteCString(0x6C000u, "/symx-creat-trunc-more-target");

        var fd = await env.Call("SysOpen", 0x6B000u, flags);
        Assert.True(fd >= 0);
        if (payload.Length > 0)
        {
            env.WriteBytes(0x6D000u, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x6D000u, (uint)payload.Length));
        }

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        var readFd = await env.Call("SysOpen", 0x6C000u, (uint)FileFlags.O_RDONLY);
        Assert.True(readFd >= 0);
        Assert.Equal(payload.Length, await env.Call("SysRead", (uint)readFd, 0x6D000u, 16));
        Assert.Equal(payload, Encoding.ASCII.GetString(env.ReadBytes(0x6D000u, payload.Length)));
        Assert.Equal(0, await env.Call("SysClose", (uint)readFd));
    }

    [Fact]
    public async Task OverlayIndirectSymlinkWithCreateFlags_WritesThroughChainedLinks()
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-create", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x50000u); // path
        env.MapUserPage(0x51000u); // payload
        env.MapUserPage(0x52000u); // read buffer
        env.WriteCString(0x50000u, path);
        env.WriteBytes(0x51000u, "q"u8.ToArray());

        Assert.Equal(12, await env.Call("SysRead", await OpenViaIndirect(env, 0x50000u, FileFlags.O_RDONLY | FileFlags.O_CREAT), 0x52000u, 16));
        Assert.Equal(12, await env.Call("SysLseek", await OpenViaIndirect(env, 0x50000u, FileFlags.O_RDONLY | FileFlags.O_CREAT), 0, 2));

        Assert.Equal(1, await env.Call("SysWrite", await OpenViaIndirect(env, 0x50000u, FileFlags.O_WRONLY | FileFlags.O_CREAT), 0x51000u, 1));
        Assert.Equal("qxxx:yyy:zzz", await ReadIndirect(env, 0x50000u, 0x52000u, 12));

        Assert.Equal(1, await env.Call("SysWrite", await OpenViaIndirect(env, 0x50000u, FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_APPEND), 0x51000u, 1));
        Assert.Equal(13, await env.Call("SysRead", await OpenViaIndirect(env, 0x50000u, FileFlags.O_RDONLY), 0x52000u, 16));

        env.WriteBytes(0x51000u, "p"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", await OpenViaIndirect(env, 0x50000u, FileFlags.O_RDWR | FileFlags.O_CREAT), 0x51000u, 1));
        Assert.Equal("pxxx:yyy:zzz", await ReadIndirect(env, 0x50000u, 0x52000u, 12));
    }

    [Theory]
    [InlineData((uint)FileFlags.O_RDONLY, "", ":xxx:yyy:zzz")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq")]
    [InlineData((uint)FileFlags.O_RDWR, "p", "pxxx:yyy:zzz")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), "r", ":xxx:yyy:zzzr")]
    public async Task OverlayIndirectSymlink_PlainRemainingModes_MatchTargetSemantics(uint flags, string payload,
        string expected)
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-plain-more", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x6E000u);
        env.MapUserPage(0x6F000u);
        env.MapUserPage(0x70000u);
        env.WriteCString(0x6E000u, path);

        if ((flags & 3u) != (uint)FileFlags.O_RDONLY || (flags & (uint)FileFlags.O_APPEND) != 0)
        {
            var fd = await OpenViaIndirect(env, 0x6E000u, (FileFlags)flags);
            env.WriteBytes(0x6F000u, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", fd, 0x6F000u, (uint)payload.Length));
            Assert.Equal(0, await env.Call("SysClose", fd));
        }

        Assert.Equal(expected, await ReadIndirect(env, 0x6E000u, 0x70000u, expected.Length));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), "", "")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC), "p", "p")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC), "r", "r")]
    public async Task OverlayIndirectSymlink_TruncRemainingModes_MatchTargetSemantics(uint flags, string payload,
        string expected)
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-trunc-more", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x71000u);
        env.MapUserPage(0x72000u);
        env.MapUserPage(0x73000u);
        env.WriteCString(0x71000u, path);

        var fd = await OpenViaIndirect(env, 0x71000u, (FileFlags)flags);
        if (payload.Length > 0)
        {
            env.WriteBytes(0x72000u, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", fd, 0x72000u, (uint)payload.Length));
        }

        Assert.Equal(0, await env.Call("SysClose", fd));
        Assert.Equal(expected, await ReadIndirect(env, 0x71000u, 0x73000u, expected.Length));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_APPEND))]
    public async Task OverlayIndirectSymlink_CreatExclRemainingModes_ReturnEexist(uint flags)
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-creat-excl-more", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x74000u);
        env.WriteCString(0x74000u, path);

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysOpen", 0x74000u, flags));
    }

    [Fact]
    public async Task OverlayIndirectSymlinkWithCreateExcl_RefusesSecondCreate()
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-creat-excl", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x53000); // path
        env.MapUserPage(0x54000u); // read buffer
        env.WriteCString(0x53000, path);

        Assert.Equal(-(int)Errno.EEXIST,
            await env.Call("SysOpen", 0x53000, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL)));
        var fd = await OpenViaIndirect(env, 0x53000, FileFlags.O_RDONLY);
        Assert.Equal(12, await env.Call("SysRead", fd, 0x54000u, 16));
        Assert.Equal(0, await env.Call("SysClose", fd));
    }

    [Fact]
    public async Task OverlayIndirectSymlinkPlain_OpenWritesReadConsistently()
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-plain", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x55000u); // path
        env.MapUserPage(0x56000u); // payload
        env.MapUserPage(0x57000u); // read buffer
        env.WriteCString(0x55000u, path);

        Assert.Equal(12, await env.Call("SysRead", await OpenViaIndirect(env, 0x55000u, FileFlags.O_RDONLY), 0x57000u, 16));
        env.WriteBytes(0x56000u, "p"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", await OpenViaIndirect(env, 0x55000u, FileFlags.O_WRONLY), 0x56000u, 1));
        Assert.Equal("pxxx:yyy:zzz", await ReadIndirect(env, 0x55000u, 0x57000u, 12));
        env.WriteBytes(0x56000u, "q"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", await OpenViaIndirect(env, 0x55000u, FileFlags.O_APPEND | FileFlags.O_WRONLY), 0x56000u, 1));
        Assert.Equal(13, await env.Call("SysRead", await OpenViaIndirect(env, 0x55000u, FileFlags.O_RDONLY), 0x57000u, 16));
    }

    [Fact]
    public async Task OverlayIndirectSymlinkTruncate_LinksDropContent()
    {
        var (root, mount, path) = CreateOverlayRootWithIndirectSymlink("indirect-trunc", ":xxx:yyy:zzz");
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x58000u); // path
        env.MapUserPage(0x59000u); // payload
        env.MapUserPage(0x5A000u); // read buffer
        env.WriteCString(0x58000u, path);
        env.WriteBytes(0x59000u, "q"u8.ToArray());

        var fd = await OpenViaIndirect(env, 0x58000u, FileFlags.O_TRUNC | FileFlags.O_WRONLY);
        Assert.Equal(1, await env.Call("SysWrite", fd, 0x59000u, 1));
        Assert.Equal(0, await env.Call("SysClose", fd));

        Assert.Equal(1, await env.Call("SysRead", await OpenViaIndirect(env, 0x58000u, FileFlags.O_RDONLY), 0x5A000u, 16));
        Assert.Equal("q", Encoding.ASCII.GetString(env.ReadBytes(0x5A000u, 1)));
    }

    [Theory]
    [InlineData("/dir", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/dir-link", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/dir-chain", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    public async Task OverlayDirectoryAndDirectorySymlink_ReadOnlyModes_Open(string path, uint flags)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x76000u);
        env.WriteCString(0x76000u, path);

        var fd = await env.Call("SysOpen", 0x76000u, flags);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/dir", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY), Errno.EISDIR)]
    public async Task OverlayDirectoryAndDirectorySymlink_WriteModes_Reject(string path, uint flags, Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x77000u);
        env.WriteCString(0x77000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x77000u, flags));
    }

    [Theory]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    public async Task OverlayDirectoryAndDirectorySymlink_WeirdModes_Reject(string path, uint flags, Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x78000u);
        env.WriteCString(0x78000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x78000u, flags));
    }

    [Theory]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    public async Task OverlayDirectoryAndDirectorySymlink_DirectoryWeirdModes_Reject(string path, uint flags,
        Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x79000u);
        env.WriteCString(0x79000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x79000u, flags));
    }

    [Theory]
    [InlineData("/dir/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/dir-link/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/dir-chain/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    public async Task OverlayDirectoryAndDirectorySymlinkWithTrailingSlash_ReadOnlyModes_Open(string path, uint flags)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x7A000u);
        env.WriteCString(0x7A000u, path);

        var fd = await env.Call("SysOpen", 0x7A000u, flags);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/dir/", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir/", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)FileFlags.O_WRONLY, Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)FileFlags.O_RDWR, Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), Errno.EISDIR)]
    public async Task OverlayDirectoryAndDirectorySymlinkWithTrailingSlash_WriteModes_Reject(string path, uint flags,
        Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x7B000u);
        env.WriteCString(0x7B000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x7B000u, flags));
    }

    [Theory]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EEXIST)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EEXIST)]
    public async Task OverlayDirectoryAndDirectorySymlinkWithTrailingSlash_WeirdModes_Reject(string path, uint flags,
        Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x7C000u);
        env.WriteCString(0x7C000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x7C000u, flags));
    }

    [Theory]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_TRUNC), Errno.EISDIR)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_TRUNC), Errno.EINVAL)]
    [InlineData("/dir-chain/", (uint)(FileFlags.O_WRONLY | FileFlags.O_DIRECTORY | FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC), Errno.EINVAL)]
    public async Task OverlayDirectoryAndDirectorySymlinkWithTrailingSlash_DirectoryWeirdModes_Reject(string path,
        uint flags, Errno errno)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x7D000u);
        env.WriteCString(0x7D000u, path);

        Assert.Equal(-(int)errno, await env.Call("SysOpen", 0x7D000u, flags));
    }

    [Fact]
    public async Task PWrite_GrowsFile_MustRefreshMappedFileBackingLength()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000); // path
        env.MapUserPage(0x31000); // first-page payload
        env.MapUserPage(0x32000); // growth payload
        env.WriteCString(0x30000, "/grow-write");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x30000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x30000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        var firstPage = new byte[LinuxConstants.PageSize];
        firstPage.AsSpan().Fill((byte)'A');
        env.WriteBytes(0x31000, firstPage);
        Assert.Equal(LinuxConstants.PageSize, await env.Call("SysWrite", (uint)fd, 0x31000, LinuxConstants.PageSize));

        const uint mapAddr = 0x50000000;
        var mmapRc = await env.Call(
            "SysMmap2",
            mapAddr,
            LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Shared | MapFlags.Fixed),
            (uint)fd);
        Assert.Equal((int)mapAddr, mmapRc);

        Assert.Equal(FaultResult.BusError,
            env.Vma.HandleFaultDetailed(mapAddr + LinuxConstants.PageSize, true, env.Engine));

        env.WriteBytes(0x32000, "Z"u8.ToArray());
        Assert.Equal(1,
            await env.Call("SysPWrite", (uint)fd, 0x32000, 1, LinuxConstants.PageSize));

        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(mapAddr + LinuxConstants.PageSize, true, env.Engine));

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv((Dentry Root, Mount Mount)? rootOverride = null)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            if (rootOverride is { } root)
            {
                SyscallManager.InitializeRoot(root.Root, root.Mount);
            }
            else
            {
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "trunc-tmpfs", null);
                var mount = new Mount(sb, sb.Root)
                {
                    Source = "tmpfs",
                    FsType = "tmpfs",
                    Options = "rw"
                };
                SyscallManager.InitializeRoot(sb.Root, mount);
            }
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public void WriteBytes(uint addr, ReadOnlySpan<byte> bytes)
        {
            Assert.True(Engine.CopyToUser(addr, bytes.ToArray()));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var buf = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithLowerFile(string name, string content)
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "trunc-overlay-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "trunc-overlay-upper", null);

        var lowerRoot = lowerSb.Root;
        var lowerFile = new Dentry(name, null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(lowerFile, 0x1A4, 0, 0);
        var lowerHandle = new LinuxFile(lowerFile, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(content.Length, lowerFile.Inode!.WriteFromHost(null, lowerHandle, Encoding.ASCII.GetBytes(content), 0));
        }
        finally
        {
            lowerHandle.Close();
        }

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "trunc-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };
        return (overlaySb.Root, mount);
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithLowerSymlink(string targetPath,
        string symlinkName, string content)
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-upper", null);

        var lowerRoot = lowerSb.Root;
        var target = new Dentry(targetPath, null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(target, 0x1A4, 0, 0);
        var writer = new LinuxFile(target, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(content.Length,
                target.Inode!.WriteFromHost(null, writer, Encoding.ASCII.GetBytes(content), 0));
        }
        finally
        {
            writer.Close();
        }

        var symlink = new Dentry(symlinkName, null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Symlink(symlink, targetPath, 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "sym-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };
        return (overlaySb.Root, mount);
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithBrokenSymlink(string symlinkName,
        string targetPath)
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-upper", null);

        var lowerRoot = lowerSb.Root;
        var symlink = new Dentry(symlinkName, null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Symlink(symlink, targetPath, 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "sym-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithDirectoryAndSymlinks()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "dir-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "dir-upper", null);

        var lowerRoot = lowerSb.Root;
        var dir = new Dentry("dir", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Mkdir(dir, 0x1ED, 0, 0);

        var child = new Dentry("a", null, dir, lowerSb);
        dir.Inode!.Create(child, 0x1A4, 0, 0);
        var writer = new LinuxFile(child, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, child.Inode!.WriteFromHost(null, writer, "x"u8.ToArray(), 0));
        }
        finally
        {
            writer.Close();
        }

        var direct = new Dentry("dir-link", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(direct, "dir", 0, 0);
        var indirect = new Dentry("dir-chain", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(indirect, "dir-link", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "dir-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }

    private static async Task<uint> OpenViaIndirect(TestEnv env, uint pathAddr, FileFlags flags)
    {
        var fd = await env.Call("SysOpen", pathAddr, (uint)flags);
        Assert.True(fd >= 0);
        return (uint)fd;
    }

    private static async Task<string> ReadIndirect(TestEnv env, uint pathAddr, uint bufferAddr, int length)
    {
        var fd = await env.Call("SysOpen", pathAddr, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        var read = await env.Call("SysRead", (uint)fd, bufferAddr, (uint)length);
        Assert.True(read >= 0);
        var result = Encoding.ASCII.GetString(env.ReadBytes(bufferAddr, read));
        await env.Call("SysClose", (uint)fd);
        return result;
    }

    private static (Dentry Root, Mount Mount, string Path) CreateOverlayRootWithIndirectSymlink(string path,
        string content)
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-upper", null);

        var lowerRoot = lowerSb.Root;
        var lowerFile = new Dentry(path, null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(lowerFile, 0x1A4, 0, 0);
        var writer = new LinuxFile(lowerFile, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(content.Length,
                lowerFile.Inode!.WriteFromHost(null, writer, Encoding.ASCII.GetBytes(content), 0));
        }
        finally
        {
            writer.Close();
        }

        var link = new Dentry("link", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Symlink(link, path, 0, 0);
        var indirect = new Dentry("indirect", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Symlink(indirect, "link", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "sym-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount, "/indirect");
    }
}
