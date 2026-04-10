using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkLinkSyscallTests
{
    [Fact]
    public async Task Link_FileSource_ToMissingTarget_CreatesSecondName()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x010000u);
        env.MapUserPage(0x011000u);
        env.WriteCString(0x010000u, "/file");
        env.WriteCString(0x011000u, "/file-copy");

        Assert.Equal(0, await env.Call("SysLink", 0x010000u, 0x011000u));
        await AssertFileContent(env, "/file", ":xxx:yyy:zzz");
        await AssertFileContent(env, "/file-copy", ":xxx:yyy:zzz");
    }

    [Theory]
    [InlineData("/missing", "/missing-a")]
    [InlineData("/missing", "/file")]
    public async Task Link_MissingSource_ReturnsEnoent_AndPreservesTarget(string oldPath, string newPath)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x012000u);
        env.MapUserPage(0x013000u);
        env.WriteCString(0x012000u, oldPath);
        env.WriteCString(0x013000u, newPath);

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x012000u, 0x013000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, oldPath));
        if (newPath == "/file")
            await AssertFileContent(env, "/file", ":xxx:yyy:zzz");
        else
            Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, newPath));
    }

    [Theory]
    [InlineData("/file", "/dir/a", ":xxx:yyy:zzz", "")]
    [InlineData("/file", "/file-new", ":xxx:yyy:zzz", "aaaa")]
    [InlineData("/file-new2", "/file", "aaaa", ":xxx:yyy:zzz")]
    [InlineData("/file", "/file", ":xxx:yyy:zzz", ":xxx:yyy:zzz")]
    [InlineData("/file-new3", "/file-new3", "aaaa", "aaaa")]
    public async Task Link_FileOverExistingOrSelf_ReturnsEexist_AndPreservesData(string oldPath, string newPath,
        string oldContent, string newContent)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));

        if (oldPath.Contains("new", StringComparison.Ordinal))
            await CreateFileWithContent(env, oldPath, "aaaa");
        if (newPath.Contains("new", StringComparison.Ordinal) && newPath != oldPath)
            await CreateFileWithContent(env, newPath, "aaaa");

        env.MapUserPage(0x014000u);
        env.MapUserPage(0x015000u);
        env.WriteCString(0x014000u, oldPath);
        env.WriteCString(0x015000u, newPath);

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysLink", 0x014000u, 0x015000u));
        await AssertFileContent(env, oldPath, oldContent);
        await AssertFileContent(env, newPath, newContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Link_SymlinkSource_PreservesSymlinkInode(bool useLinkat)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.MapUserPage(0x11000u);
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x10000u, "/link");
        env.WriteCString(0x11000u, "/copy");
        env.WriteCString(0x12000u, "/copy");
        env.WriteCString(0x13000u, "/copy");

        Assert.Equal(0, await Link(env, 0x10000u, 0x11000u, useLinkat));

        var rc = await env.Call("SysReadlink", 0x12000u, 0x13000u, 64);
        Assert.Equal(4, rc);
        Assert.Equal("file", Encoding.ASCII.GetString(env.ReadBytes(0x13000u, rc)));

        await AssertFileContents(env, 0x12000u, ":xxx:yyy:zzz");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Link_BrokenSymlinkSource_CreatesAnotherBrokenSymlink(bool useLinkat)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x14000u);
        env.MapUserPage(0x15000u);
        env.MapUserPage(0x16000u);
        env.MapUserPage(0x17000u);
        env.WriteCString(0x14000u, "/broken");
        env.WriteCString(0x15000u, "/broken-copy");
        env.WriteCString(0x16000u, "/broken-copy");
        env.WriteCString(0x17000u, "/broken-copy");

        Assert.Equal(0, await Link(env, 0x14000u, 0x15000u, useLinkat));

        var rc = await env.Call("SysReadlink", 0x16000u, 0x17000u, 64);
        Assert.Equal(15, rc);
        Assert.Equal("/missing-target", Encoding.ASCII.GetString(env.ReadBytes(0x17000u, rc)));

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x16000u, (uint)FileFlags.O_RDONLY));
    }

    [Theory]
    [InlineData("/missing", "/link")]
    [InlineData("/link", "/dir/a")]
    [InlineData("/link", "/file-new-link")]
    [InlineData("/file-new-over-link", "/link")]
    [InlineData("/link", "/link")]
    [InlineData("/link", "/broken")]
    public async Task Link_SymlinkAndTargetConflictCases_ReturnExpectedAndPreserveData(string oldPath, string newPath)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));

        if (oldPath.Contains("new", StringComparison.Ordinal))
            await CreateFileWithContent(env, oldPath, "aaaa");
        if (newPath.Contains("new", StringComparison.Ordinal))
            await CreateFileWithContent(env, newPath, "aaaa");

        env.MapUserPage(0x171000u);
        env.MapUserPage(0x172000u);
        env.MapUserPage(0x173000u);
        env.MapUserPage(0x174000u);
        env.WriteCString(0x171000u, oldPath);
        env.WriteCString(0x172000u, newPath);
        env.WriteCString(0x173000u, "/broken");
        env.WriteCString(0x174000u, "/link");

        var expectedErrno = oldPath == "/missing" ? -(int)Errno.ENOENT : -(int)Errno.EEXIST;
        Assert.Equal(expectedErrno, await env.Call("SysLink", 0x171000u, 0x172000u));

        if (oldPath == "/missing")
        {
            Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, oldPath));
        }
        else if (oldPath.Contains("new", StringComparison.Ordinal))
        {
            await AssertFileContent(env, oldPath, "aaaa");
        }
        else
        {
            await AssertFileContent(env, oldPath, ":xxx:yyy:zzz");
        }

        if (newPath == "/broken")
        {
            var rc = await env.Call("SysReadlink", 0x173000u, 0x171000u, 64);
            Assert.Equal(15, rc);
            Assert.Equal("/missing-target", Encoding.ASCII.GetString(env.ReadBytes(0x171000u, rc)));
        }
        else if (newPath == "/link")
        {
            await AssertFileContent(env, "/link", ":xxx:yyy:zzz");
        }
        else if (newPath.Contains("new", StringComparison.Ordinal))
        {
            await AssertFileContent(env, newPath, "aaaa");
        }
        else if (newPath == "/dir/a")
        {
            await AssertFileContent(env, newPath, "");
        }
    }

    [Fact]
    public async Task Linkat_WithAtSymlinkFollow_LinksResolvedTargetInsteadOfSymlink()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x18000u);
        env.MapUserPage(0x19000u);
        env.MapUserPage(0x1A000u);
        env.MapUserPage(0x1B000u);
        env.WriteCString(0x18000u, "/link");
        env.WriteCString(0x19000u, "/follow-copy");
        env.WriteCString(0x1A000u, "/follow-copy");
        env.WriteCString(0x1B000u, "/follow-copy");

        Assert.Equal(0, await env.Call("SysLinkat", LinuxConstants.AT_FDCWD, 0x18000u, LinuxConstants.AT_FDCWD,
            0x19000u, LinuxConstants.AT_SYMLINK_FOLLOW));

        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysReadlink", 0x1A000u, 0x1B000u, 64));
        await AssertFileContents(env, 0x1A000u, ":xxx:yyy:zzz");
    }

    [Theory]
    [InlineData("/file/sub", -(int)Errno.ENOTDIR, false)]
    [InlineData("/link/sub", -(int)Errno.ENOTDIR, false)]
    [InlineData("/indirect/sub", -(int)Errno.ENOTDIR, false)]
    [InlineData("/broken/sub", -(int)Errno.ENOENT, false)]
    [InlineData("/missing/sub", -(int)Errno.ENOENT, false)]
    [InlineData("/file/sub", -(int)Errno.ENOTDIR, true)]
    [InlineData("/link/sub", -(int)Errno.ENOTDIR, true)]
    [InlineData("/indirect/sub", -(int)Errno.ENOTDIR, true)]
    [InlineData("/broken/sub", -(int)Errno.ENOENT, true)]
    [InlineData("/missing/sub", -(int)Errno.ENOENT, true)]
    public async Task Link_OldPathBoundaryErrors_ArePreserved(string oldPath, int errno, bool useLinkat)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1C000u);
        env.MapUserPage(0x1D000u);
        env.WriteCString(0x1C000u, oldPath);
        env.WriteCString(0x1D000u, "/new-link");

        Assert.Equal(errno, await Link(env, 0x1C000u, 0x1D000u, useLinkat));
    }

    [Fact]
    public async Task Link_DirectorySource_ReturnsEperm()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1E000u);
        env.MapUserPage(0x1F000u);
        env.WriteCString(0x1E000u, "/dir");
        env.WriteCString(0x1F000u, "/dir-copy");

        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysLink", 0x1E000u, 0x1F000u));
    }

    [Fact]
    public async Task Linkat_WithAtSymlinkFollow_ToDirectorySymlink_ReturnsEperm()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x203000u);
        env.MapUserPage(0x204000u);
        env.WriteCString(0x203000u, "/dir-link");
        env.WriteCString(0x204000u, "/dir-link-copy");

        // Link with AT_SYMLINK_FOLLOW on a symlink to directory should return EPERM
        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysLinkat", LinuxConstants.AT_FDCWD, 0x203000u, LinuxConstants.AT_FDCWD,
            0x204000u, LinuxConstants.AT_SYMLINK_FOLLOW));
    }

    [Theory]
    [InlineData("/dir", "/file")]
    [InlineData("/file", "/dir")]
    [InlineData("/dir", "/dir")]
    [InlineData("/dir", "/dir/subdir")]
    public async Task Link_SourceOverExistingTarget_ReturnsEexist(string oldPath, string newPath)
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x21000u);
        env.MapUserPage(0x22000u);
        env.WriteCString(0x21000u, oldPath);
        env.WriteCString(0x22000u, newPath);

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysLink", 0x21000u, 0x22000u));
    }

    [Fact]
    public async Task Link_DirectorySource_OverParentDirectory_ReturnsEexist()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x23000u);
        env.MapUserPage(0x24000u);
        env.WriteCString(0x23000u, "/dir/pop");
        env.WriteCString(0x24000u, "/dir");

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysLink", 0x23000u, 0x24000u));
        Assert.True(await OpenDirectory(env, "/dir/pop") >= 0);
        Assert.True(await OpenDirectory(env, "/dir") >= 0);
    }

    [Fact]
    public async Task Link_UnlinkedFile_ReturnsEnoent_ForBothNames()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x25000u);
        env.MapUserPage(0x26000u);
        env.WriteCString(0x25000u, "/file");
        env.WriteCString(0x26000u, "/gone-copy");

        Assert.Equal(0, await env.Call("SysUnlink", 0x25000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x25000u, 0x26000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/file"));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/gone-copy"));
    }

    [Fact]
    public async Task Link_RenamedFile_UsesNewName_AndOldNameReturnsEnoent()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x27000u);
        env.MapUserPage(0x28000u);
        env.MapUserPage(0x29000u);
        env.WriteCString(0x27000u, "/file");
        env.WriteCString(0x28000u, "/file-renamed");
        env.WriteCString(0x29000u, "/file-copy");

        Assert.Equal(0, await env.Call("SysRename", 0x27000u, 0x28000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/file"));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x27000u, 0x29000u));
        Assert.Equal(0, await env.Call("SysLink", 0x28000u, 0x27000u));

        await AssertFileContent(env, "/file", ":xxx:yyy:zzz");
        await AssertFileContent(env, "/file-renamed", ":xxx:yyy:zzz");
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/file-copy"));
    }

    [Fact]
    public async Task Link_UnlinkedSymlink_ReturnsEnoent_ForBothNames()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x291000u);
        env.MapUserPage(0x292000u);
        env.WriteCString(0x291000u, "/link");
        env.WriteCString(0x292000u, "/link-copy");

        Assert.Equal(0, await env.Call("SysUnlink", 0x291000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x291000u, 0x292000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/link"));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/link-copy"));
    }

    [Fact]
    public async Task Link_RenamedSymlink_UsesNewName_AndOldNameReturnsEnoent()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x293000u);
        env.MapUserPage(0x294000u);
        env.MapUserPage(0x295000u);
        env.MapUserPage(0x296000u);
        env.WriteCString(0x293000u, "/link");
        env.WriteCString(0x294000u, "/link-renamed");
        env.WriteCString(0x295000u, "/link-copy");
        env.WriteCString(0x296000u, "/link");

        Assert.Equal(0, await env.Call("SysRename", 0x293000u, 0x294000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/link"));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x293000u, 0x295000u));
        Assert.Equal(0, await env.Call("SysLink", 0x294000u, 0x296000u));

        await AssertFileContent(env, "/link", ":xxx:yyy:zzz");
        await AssertFileContent(env, "/link-renamed", ":xxx:yyy:zzz");
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/link-copy"));
    }

    [Fact]
    public async Task Link_RemovedDirectory_ReturnsEnoent()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x2A000u);
        env.MapUserPage(0x2B000u);
        env.WriteCString(0x2A000u, "/empty");
        env.WriteCString(0x2B000u, "/empty-copy");

        Assert.Equal(0, await env.Call("SysRmdir", 0x2A000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x2A000u, 0x2B000u));
        Assert.Equal(-(int)Errno.ENOENT, await OpenDirectory(env, "/empty"));
        Assert.Equal(-(int)Errno.ENOENT, await OpenDirectory(env, "/empty-copy"));
    }

    [Fact]
    public async Task Link_RenamedDirectory_UsesPriorityFromUpstream()
    {
        var (root, mount) = CreateOverlayRootWithLinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x2C000u);
        env.MapUserPage(0x2D000u);
        env.MapUserPage(0x2E000u);
        env.WriteCString(0x2C000u, "/empty/new");
        env.WriteCString(0x2D000u, "/dir-new");
        env.WriteCString(0x2E000u, "/dir-new-a");

        Assert.Equal(0, await env.Call("SysMkdir", 0x2C000u, 0x1ED));
        Assert.Equal(0, await env.Call("SysRename", 0x2C000u, 0x2D000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysLink", 0x2C000u, 0x2E000u));
        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysLink", 0x2D000u, 0x2C000u));

        Assert.Equal(-(int)Errno.ENOENT, await OpenDirectory(env, "/empty/new"));
        Assert.True(await OpenDirectory(env, "/dir-new") >= 0);
        Assert.Equal(-(int)Errno.ENOENT, await OpenDirectory(env, "/dir-new-a"));
    }

    private static async Task<int> Link(TestEnv env, uint oldPathAddr, uint newPathAddr, bool useLinkat)
    {
        return useLinkat
            ? await env.Call("SysLinkat", LinuxConstants.AT_FDCWD, oldPathAddr, LinuxConstants.AT_FDCWD,
                newPathAddr, 0)
            : await env.Call("SysLink", oldPathAddr, newPathAddr);
    }

    private static async Task AssertFileContents(TestEnv env, uint pathAddr, string expected)
    {
        env.MapUserPage(0x20000u);
        var fd = await env.Call("SysOpen", pathAddr, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        var rc = await env.Call("SysRead", (uint)fd, 0x20000u, 64);
        Assert.Equal(expected.Length, rc);
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x20000u, rc)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private static async Task AssertFileContent(TestEnv env, string path, string expected)
    {
        env.MapUserPage(0x2F000u);
        env.MapUserPage(0x30000u);
        env.WriteCString(0x2F000u, path);
        var fd = await env.Call("SysOpen", 0x2F000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        var rc = await env.Call("SysRead", (uint)fd, 0x30000u, 64);
        Assert.Equal(expected.Length, rc);
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x30000u, rc)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private static async Task<int> OpenPath(TestEnv env, string path)
    {
        env.MapUserPage(0x31000u);
        env.WriteCString(0x31000u, path);
        return await env.Call("SysOpen", 0x31000u, (uint)FileFlags.O_RDONLY);
    }

    private static async Task<int> OpenDirectory(TestEnv env, string path)
    {
        env.MapUserPage(0x32000u);
        env.WriteCString(0x32000u, path);
        var fd = await env.Call("SysOpen", 0x32000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        if (fd >= 0)
            Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        return fd;
    }

    private static async Task CreateFileWithContent(TestEnv env, string path, string content)
    {
        env.MapUserPage(0x33000u);
        env.MapUserPage(0x34000u);
        env.WriteCString(0x33000u, path);
        env.WriteBytes(0x34000u, Encoding.ASCII.GetBytes(content));
        var fd = await env.Call("SysOpen", 0x33000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        Assert.Equal(content.Length, await env.Call("SysWrite", (uint)fd, 0x34000u, (uint)content.Length));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly HashSet<uint> _mappedPages = [];

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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-link-tmpfs", null);
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
            if (!_mappedPages.Add(addr))
                return;

            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var buf = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
        }

        public void WriteBytes(uint addr, byte[] data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
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

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithLinkFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "link-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "link-upper", null);

        var lowerRoot = lowerSb.Root;

        var file = new Dentry("file", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(file, 0x1A4, 0, 0);
        var fileWriter = new LinuxFile(file, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(12, file.Inode!.WriteFromHost(null, fileWriter, Encoding.ASCII.GetBytes(":xxx:yyy:zzz"), 0));
        }
        finally
        {
            fileWriter.Close();
        }

        var dir = new Dentry("dir", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(dir, 0x1ED, 0, 0);
        var child = new Dentry("a", null, dir, lowerSb);
        dir.Inode!.Create(child, 0x1A4, 0, 0);
        var subdir = new Dentry("subdir", null, dir, lowerSb);
        dir.Inode.Mkdir(subdir, 0x1ED, 0, 0);
        var pop = new Dentry("pop", null, dir, lowerSb);
        dir.Inode.Mkdir(pop, 0x1ED, 0, 0);

        var empty = new Dentry("empty", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(empty, 0x1ED, 0, 0);

        lowerRoot.Inode.Symlink(new Dentry("link", null, lowerRoot, lowerSb), "file"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("indirect", null, lowerRoot, lowerSb), "link"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("broken", null, lowerRoot, lowerSb), "/missing-target"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("dir-link", null, lowerRoot, lowerSb), "dir"u8.ToArray(), 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "link-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }
}
