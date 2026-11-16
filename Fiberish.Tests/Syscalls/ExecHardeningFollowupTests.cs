using System.Buffers.Binary;
using System.Text;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public partial class ExecSemanticsRegressionTests
{
    [Theory]
    [InlineData(0UL, 128 * 1024)]
    [InlineData(64UL * 1024, 128 * 1024)]
    [InlineData(8UL * 1024 * 1024, 2 * 1024 * 1024)]
    [InlineData(ulong.MaxValue, 6 * 1024 * 1024)]
    public void ExecArgumentLimit_UsesFloorQuarterAndUnlimitedCap(ulong soft, int expected)
    {
        using var env = ExecEnv.CreateTmpfs(9610);
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_STACK, new ResourceLimit(soft, ulong.MaxValue));
        Assert.Equal(expected, env.Syscalls.GetExecArgMaxBytes());
    }

    [Fact]
    public async Task LoadExecutable_InterpreterChecks_DoNotRequireTaskOwner()
    {
        using var env = ExecEnv.CreateTmpfs(9611);
        await MountExecFixture(env, "/ld", LinuxConstants.MS_NOEXEC);
        await WriteMountedExecFixture(env, "/ld/loader", TinyExecElf(dynamic: true), 0x1ed);
        env.WriteFile("/image", TinyExecElf(interp: "/ld/loader"), 0x1ed);
        var loc = env.Syscalls.ResolvePath("/image").loc;
        env.Engine.Owner = null;
        var error = Assert.Throws<ExecFileException>(() =>
            env.Process.LoadExecutable(loc.Dentry!, "/image", ["/image"], [], loc.Mount!));
        Assert.Equal(Errno.EACCES, error.Error);
    }

    [Theory]
    [InlineData(0u, 0x1ed, false, false, false, 0)]
    [InlineData(0u, 0x49, true, false, false, 0)] // execute-only, no read DAC
    [InlineData(0u, 0x1a4, false, false, false, -(int)Errno.EACCES)]
    [InlineData(0u, 0x1c0, true, false, false, -(int)Errno.EACCES)]
    [InlineData(0u, 0x1ed, true, false, true, -(int)Errno.EACCES)]
    [InlineData(0u, 0x1ed, false, true, false, -(int)Errno.ETXTBSY)]
    [InlineData(LinuxConstants.MS_NOEXEC, 0x1ed, false, false, false, -(int)Errno.EACCES)]
    [InlineData(LinuxConstants.MS_NOEXEC, 0x1ed, false, true, false, -(int)Errno.EACCES)]
    public async Task SysExecve_ElfInterpreter_UsesOpenExecChecks(uint flags, int mode,
        bool unprivileged, bool writer, bool privateDirectory, int expected)
    {
        using var env = ExecEnv.CreateTmpfs(9601);
        await MountExecFixture(env, "/ld", flags);
        await WriteMountedExecFixture(env, "/ld/loader", TinyExecElf(dynamic: true), mode);
        env.WriteFile("/image", TinyExecElf(interp: "/ld/loader"), 0x1ed);
        var loader = env.Syscalls.ResolvePath("/ld/loader").loc;
        int fd = -1;
        if (writer)
        {
            env.WriteString(PathAddr, "/ld/loader");
            fd = await Call(env.Syscalls, "SysOpen", PathAddr, (uint)FileFlags.O_RDWR);
            Assert.True(fd >= 0);
            env.Syscalls.SetFdCloseOnExec(fd, true);
        }
        if (privateDirectory)
            env.Syscalls.ResolvePath("/ld").loc.Dentry!.Inode!.Mode = 0x1c0;
        if (unprivileged)
            CredentialService.InitializeCredentials(env.Process, 1000, 1000);
        env.WriteString(PathAddr, "/image");
        var oldMem = env.Process.Mem;
        var rc = await Call(env.Syscalls, "SysExecve", PathAddr);
        Assert.Equal(expected, rc);
        Assert.False(env.Task.Exited);
        if (expected < 0)
        {
            Assert.Same(oldMem, env.Process.Mem);
            if (fd >= 0) Assert.NotNull(env.Syscalls.GetFD(fd));
        }
        Assert.Equal(writer, loader.Dentry!.Inode!.HasWritableOpenFile);
    }

    [Theory]
    [InlineData(0u, 0x9ed, 1001, 1000)] // 04755
    [InlineData(0u, 0x5ed, 1000, 1002)] // 02755
    [InlineData(0u, 0x5e5, 1000, 1000)] // 02745, no group execute
    [InlineData(0u, 0xfed, 1001, 1002)] // 06755
    [InlineData(0u, 0xfe5, 1001, 1000)] // 06745
    [InlineData(LinuxConstants.MS_NOSUID, 0x9ed, 1000, 1000)]
    [InlineData(LinuxConstants.MS_NOSUID, 0x5ed, 1000, 1000)]
    [InlineData(LinuxConstants.MS_NOSUID, 0xfed, 1000, 1000)]
    public async Task SysExecve_SetId_UsesMountAndGroupExecute(uint flags, int mode, int euid, int egid)
    {
        using var env = ExecEnv.CreateTmpfs(9602, flags);
        env.WriteFile("/image", TinyExecElf(), mode);
        var inode = env.Syscalls.ResolvePath("/image").loc.Dentry!.Inode!;
        inode.Uid = 1001;
        inode.Gid = 1002;
        CredentialService.InitializeCredentials(env.Process, 1000, 1000);
        env.WriteString(PathAddr, "/image");

        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Equal(euid, env.Process.EUID);
        Assert.Equal(egid, env.Process.EGID);
        Assert.Equal(euid, env.Process.SUID);
        Assert.Equal(egid, env.Process.SGID);
        Assert.Equal(euid, env.Process.FSUID);
        Assert.Equal(egid, env.Process.FSGID);
        var auxv = ReadAuxv(env.Engine, env.Engine.RegRead(Reg.ESP));
        Assert.Equal((uint)euid, auxv[LinuxConstants.AT_EUID]);
        Assert.Equal((uint)egid, auxv[LinuxConstants.AT_EGID]);
        Assert.Equal(euid != 1000 || egid != 1000 ? 1u : 0u, auxv[LinuxConstants.AT_SECURE]);
    }

    [Theory]
    [InlineData(0u, LinuxConstants.MS_NOSUID, 1001)]
    [InlineData(LinuxConstants.MS_NOSUID, 0u, 1000)]
    public async Task SysExecve_SetId_DependsOnMainMountNotPtInterp(uint mainFlags, uint interpFlags, int euid)
    {
        using var env = ExecEnv.CreateTmpfs(9603, mainFlags);
        await MountExecFixture(env, "/ld", interpFlags);
        await WriteMountedExecFixture(env, "/ld/loader", TinyExecElf(dynamic: true), 0xfed);
        env.WriteFile("/image", TinyExecElf(interp: "/ld/loader"), 0x9ed);
        env.Syscalls.ResolvePath("/image").loc.Dentry!.Inode!.Uid = 1001;
        var ld = env.Syscalls.ResolvePath("/ld/loader").loc.Dentry!.Inode!;
        ld.Uid = 1003;
        ld.Gid = 1004;
        CredentialService.InitializeCredentials(env.Process, 1000, 1000);
        env.WriteString(PathAddr, "/image");
        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Equal(euid, env.Process.EUID);
        Assert.Equal(1000, env.Process.EGID);
        Assert.Equal((uint)euid, ReadAuxv(env.Engine, env.Engine.RegRead(Reg.ESP))[LinuxConstants.AT_EUID]);
    }

    [Theory]
    [InlineData(36, 0, 4095, 1024 * 1024, 0)]
    [InlineData(24, 24, 4095, 1024 * 1024, 0)]
    [InlineData(36, 36, 4095, 1024 * 1024, -(int)Errno.E2BIG)]
    [InlineData(48, 24, 4095, 1024 * 1024, -(int)Errno.E2BIG)]
    [InlineData(24, 48, 4095, 1024 * 1024, -(int)Errno.E2BIG)]
    [InlineData(28000, 28000, 0, 1024 * 1024, -(int)Errno.E2BIG)]
    [InlineData(12, 12, 4095, 256 * 1024, 0)]
    [InlineData(18, 18, 4095, 256 * 1024, -(int)Errno.E2BIG)]
    [InlineData(1, 1, 65535, 256 * 1024, -(int)Errno.E2BIG)] // filename/pointers count too
    public async Task SysExecve_CombinedBudget_IncludesBothVectorsAndFloor(int argc, int envc,
        int length, int stack, int expected)
    {
        using var env = ExecEnv.CreateTmpfs(9604);
        env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_STACK, new ResourceLimit((uint)stack, uint.MaxValue));
        env.WriteFile("/image", TinyExecElf(), 0x1ed);
        env.WriteString(PathAddr, "/image");
        env.MapRange(StrAddr, (uint)length + 1);
        env.WriteString(StrAddr, new string('x', length));
        WriteRepeatedExecVector(env, 0x63000000, argc, StrAddr);
        WriteRepeatedExecVector(env, 0x64000000, envc, StrAddr);
        var old = env.Process.Mem;
        Assert.Equal(expected, await Call(env.Syscalls, "SysExecve", PathAddr, 0x63000000, 0x64000000));
        Assert.False(env.Task.Exited);
        if (expected != 0) Assert.Same(old, env.Process.Mem);
    }

    [Theory]
    [InlineData(MapFlags.Shared, Protection.Read | Protection.Write)]
    [InlineData(MapFlags.Shared, Protection.Read)]
    [InlineData(MapFlags.Private, Protection.Read | Protection.Write)]
    [InlineData(MapFlags.Private, Protection.Read)]
    public async Task SysExecve_MappingWriter_SurvivesCloseMprotectAndSplit(MapFlags flags, Protection prot)
    {
        using var env = ExecEnv.CreateTmpfs(9605);
        env.WriteFile("/image", TinyExecElf(), 0x1ed);
        env.WriteString(PathAddr, "/image");
        var inode = env.Syscalls.ResolvePath("/image").loc.Dentry!.Inode!;
        var fd = await Call(env.Syscalls, "SysOpen", PathAddr, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        const uint map = 0x65000000;
        Assert.Equal((int)map, await Call(env.Syscalls, "SysMmap2", map, 8192, (uint)prot,
            (uint)(flags | MapFlags.Fixed), (uint)fd));
        Assert.Equal(0, await Call(env.Syscalls, "SysClose", (uint)fd));
        Assert.True(inode.HasWritableOpenFile);
        Assert.Equal(-(int)Errno.ETXTBSY, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Equal(0, await Call(env.Syscalls, "SysMprotect", map, 4096, (uint)Protection.Read));
        Assert.Equal(0, await Call(env.Syscalls, "SysMunmap", map, 4096));
        Assert.True(inode.HasWritableOpenFile);
        Assert.Equal(-(int)Errno.ETXTBSY, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Equal(0, await Call(env.Syscalls, "SysMunmap", map + 4096, 4096));
        Assert.False(inode.HasWritableOpenFile);
        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));
    }

    [Fact]
    public async Task SysExecve_BadInterpreter_ReturnsElibbadAndPreservesImage()
    {
        using var env = ExecEnv.CreateTmpfs(9606);
        env.WriteFile("/loader", new byte[4096], 0x1ed);
        env.WriteFile("/image", TinyExecElf(interp: "/loader"), 0x1ed);
        env.WriteString(PathAddr, "/image");
        var old = env.Process.Mem;
        Assert.Equal(-(int)Errno.ELIBBAD, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.Same(old, env.Process.Mem);
        Assert.Same(old, env.Syscalls.Mem);
        Assert.False(env.Task.Exited);
    }

    [Theory]
    [InlineData((int)ExecCommitCheckpoint.BeforeFdUnshare, false)]
    [InlineData((int)ExecCommitCheckpoint.BeforeThreadTeardown, true)]
    [InlineData((int)ExecCommitCheckpoint.BeforeAddressSpaceSwitch, true)]
    [InlineData((int)ExecCommitCheckpoint.AfterAddressSpaceSwitch, true)]
    public async Task SysExecve_InjectedCommitFailure_RollsBackOrTerminates(int checkpoint, bool fatal)
    {
        using var env = ExecEnv.CreateTmpfs(9607);
        env.WriteFile("/image", TinyExecElf(), 0x1ed);
        env.WriteString(PathAddr, "/image");
        var oldComm = env.Process.Comm;
        var oldMem = env.Process.Mem;
        var oldPath = env.Process.ExecutablePathRaw;
        var oldArgs = env.Process.CommandLineArgumentBytes;
        var oldRaw = env.Process.CommandLineRaw;
        var oldLayout = oldMem.Layout;
        var oldBrk = env.Syscalls.BrkAddr;
        var oldSigReturn = env.Syscalls.SigReturnAddr;
        var oldRtSigReturn = env.Syscalls.RtSigReturnAddr;
        var loc = env.Syscalls.ResolvePath("/image").loc;
        var fd = env.Syscalls.AllocFD(new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount),
            closeOnExec: true, task: env.Task);
        Assert.True(fd >= 0);
        env.Task.SignalMask = 1UL << ((int)Signal.SIGSEGV - 1);
        env.Process.SignalActions[(int)Signal.SIGSEGV] = new SigAction { Handler = 1 };
        env.Process.ExecCommitFaultForTesting = stage =>
        {
            if ((int)stage == checkpoint) throw new OutOfMemoryException("injected exec commit failure");
        };
        var result = await Call(env.Syscalls, "SysExecve", PathAddr);
        if (fatal)
        {
            Assert.Equal(0, result);
            Assert.True(env.Task.Exited);
            Assert.Equal(FiberTaskStatus.Terminated, env.Task.Status);
            Assert.True(env.Process.ExitedBySignal);
            Assert.Equal((int)Signal.SIGSEGV, env.Process.TermSignal);
        }
        else
        {
            Assert.Equal(-(int)Errno.ENOMEM, result);
            Assert.False(env.Task.Exited);
            Assert.Same(oldMem, env.Process.Mem);
            Assert.Same(oldMem, env.Syscalls.Mem);
            Assert.Same(oldPath, env.Process.ExecutablePathRaw);
            Assert.Same(oldArgs, env.Process.CommandLineArgumentBytes);
            Assert.Same(oldRaw, env.Process.CommandLineRaw);
            Assert.Equal(oldComm, env.Process.Comm);
            Assert.Same(oldLayout, env.Process.Mem.Layout);
            Assert.Equal(oldBrk, env.Syscalls.BrkAddr);
            Assert.Equal(oldSigReturn, env.Syscalls.SigReturnAddr);
            Assert.Equal(oldRtSigReturn, env.Syscalls.RtSigReturnAddr);
            Assert.NotNull(env.Syscalls.GetFD(fd));
            Assert.True(env.Syscalls.IsFdCloseOnExec(fd));
        }
    }

    [Fact]
    public async Task SysExecve_InvalidLoad_IsFatalEvenWhenSegvIgnoredAndBlocked()
    {
        using var env = ExecEnv.CreateTmpfs(9608);
        var bytes = TinyExecElf();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52 + 20, 4), 1); // filesz > memsz
        env.WriteFile("/image", bytes, 0x1ed);
        env.WriteString(PathAddr, "/image");
        env.Task.SignalMask = 1UL << ((int)Signal.SIGSEGV - 1);
        env.Process.SignalActions[(int)Signal.SIGSEGV] = new SigAction { Handler = 1 };
        Assert.Equal(0, await Call(env.Syscalls, "SysExecve", PathAddr));
        Assert.True(env.Task.Exited);
        Assert.True(env.Process.ExitedBySignal);
        Assert.Equal((int)Signal.SIGSEGV, env.Process.TermSignal);
    }

    [Fact]
    public void ExecHotChecksAndUnsharedFdTable_DoNotAllocate()
    {
        using var env = ExecEnv.CreateTmpfs(9609);
        env.WriteFile("/image", TinyExecElf(), 0x1ed);
        var location = env.Syscalls.ResolvePath("/image").loc;
        var fd = env.Syscalls.AllocFD(new LinuxFile(location.Dentry!, FileFlags.O_RDONLY, location.Mount), task: env.Task);
        Assert.True(fd >= 0);
        var file = env.Syscalls.GetFD(fd);
        env.MapRange(ArgvAddr, 4096);
        Assert.Equal(0, env.Syscalls.ReadUserCStringVectorBytes(ArgvAddr).Error);
        for (var i = 0; i < 32; i++)
        {
            env.Process.CheckExecutableAccess(location.Dentry!, location.Mount!);
            CredentialService.ComputeExecIdentity(env.Process, location.Dentry!.Inode!);
            env.Syscalls.UnshareFdTable(env.Task);
            env.Syscalls.CloseExecDescriptors();
            env.Syscalls.ReadUserCStringVectorBytes(ArgvAddr);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            env.Process.CheckExecutableAccess(location.Dentry!, location.Mount!);
            CredentialService.ComputeExecIdentity(env.Process, location.Dentry!.Inode!);
            env.Syscalls.UnshareFdTable(env.Task);
            env.Syscalls.CloseExecDescriptors();
            env.Syscalls.ReadUserCStringVectorBytes(ArgvAddr);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(file, env.Syscalls.GetFD(fd));
    }

    private static async Task MountExecFixture(ExecEnv env, string path, uint flags)
    {
        env.WriteString(PathAddr, path);
        Assert.Equal(0, await Call(env.Syscalls, "SysMkdir", PathAddr, 0x1ed));
        env.WriteString(StrAddr, "tmpfs");
        Assert.Equal(0, await Call(env.Syscalls, "SysMount", 0, PathAddr, StrAddr, flags));
    }

    private static async Task WriteMountedExecFixture(ExecEnv env, string path, byte[] contents, int mode)
    {
        // WriteGuestFile is a bootstrap helper rooted in the root superblock;
        // use real guest open/write for a fixture on a child mount.
        env.WriteString(PathAddr, path);
        var fd = await Call(env.Syscalls, "SysOpen", PathAddr,
            (uint)(FileFlags.O_CREAT | FileFlags.O_TRUNC | FileFlags.O_WRONLY), (uint)mode);
        Assert.True(fd >= 0);
        env.MapRange(StrAddr, (uint)contents.Length);
        env.WriteBytes(StrAddr, contents);
        Assert.Equal(contents.Length, await Call(env.Syscalls, "SysWrite", (uint)fd, StrAddr, (uint)contents.Length));
        Assert.Equal(0, await Call(env.Syscalls, "SysClose", (uint)fd));
    }

    private static void WriteRepeatedExecVector(ExecEnv env, uint addr, int count, uint str)
    {
        env.MapRange(addr, (uint)(count + 1) * 4);
        for (var i = 0; i < count; i++) env.WriteU32(addr + (uint)i * 4, str);
        env.WriteU32(addr + (uint)count * 4, 0);
    }

    private static byte[] TinyExecElf(string? interp = null, bool dynamic = false)
    {
        var image = new byte[4096];
        "\x7f"u8.CopyTo(image);
        image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
        image[4] = 1; image[5] = 1; image[6] = 1;
        var baseAddr = dynamic ? 0u : 0x08048000u;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), dynamic ? (ushort)3 : (ushort)2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), baseAddr + 512);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(40), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(42), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(44), interp == null ? (ushort)1 : (ushort)2);
        WriteProgramHeader(image.AsSpan(52, 32), 1, 0, baseAddr, 4096, 4096, 5);
        if (interp != null)
        {
            var path = Encoding.UTF8.GetBytes(interp + '\0');
            WriteProgramHeader(image.AsSpan(84, 32), 3, 128, 0, (uint)path.Length, (uint)path.Length, 4);
            path.CopyTo(image, 128);
        }
        new byte[] { 0xb8, 1, 0, 0, 0, 0xbb, 42, 0, 0, 0, 0xcd, 0x80 }.CopyTo(image, 512);
        return image;
    }
}
