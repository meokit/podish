using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class InotifyAddWatchValidationTests
{
    [Theory]
    [InlineData(0x00001000U)]
    [InlineData(0x00010000U)]
    [InlineData(0x00800000U)]
    [InlineData(0x08000000U)]
    public void UnknownMaskBits_AreRejectedWithoutUpdatingWatch(uint unknown)
    {
        using var env = new SyscallTestEnv();
        var fd = env.Invoke("SysInotifyInit1");
        env.PutString(0x10000, "/");
        var wd = env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000, LinuxConstants.IN_OPEN);
        Assert.True(wd > 0);
        Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000,
            LinuxConstants.IN_CLOSE_NOWRITE | unknown));
        var file = env.Invoke("SysOpen", 0x10000);
        Assert.True(file >= 0);
        env.Sm.FreeFD(file);
        var watch = Assert.IsType<Fiberish.Syscalls.InotifyInode>(env.Sm.GetFD(fd)!.OpenedInode);
        uint[] expected = [LinuxConstants.IN_OPEN | LinuxConstants.IN_ISDIR];
        Assert.Equal(expected, env.ReadMasks(watch));
    }

    [Theory]
    [InlineData(LinuxConstants.IN_ISDIR)]
    [InlineData(LinuxConstants.IN_UNMOUNT)]
    [InlineData(LinuxConstants.IN_IGNORED)]
    [InlineData(LinuxConstants.IN_Q_OVERFLOW)]
    public void KnownOutputBits_AreAcceptedAlongsideEventBits(uint outputBit)
    {
        using var env = new SyscallTestEnv();
        var fd = env.Invoke("SysInotifyInit1");
        env.PutString(0x10000, "/");
        Assert.True(env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000, LinuxConstants.IN_OPEN | outputBit) > 0);
    }

    [Fact]
    public void MaskWithoutEventBits_ReturnsEINVAL()
    {
        using var env = new SyscallTestEnv();
        var fd = env.Invoke("SysInotifyInit1", 0);
        Assert.True(fd >= 0);
        env.PutString(0x10000, "/");

        Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000, 0));
        Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000,
            LinuxConstants.IN_MASK_CREATE | LinuxConstants.IN_MASK_ADD | LinuxConstants.IN_MODIFY));

        env.Sm.FreeFD(fd);
    }

    [Fact]
    public void NonRootCannotWatchPrivateRootOwnedFile()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/private", "secret"u8);
        var inode = env.Sm.PathWalk("/private").Dentry!.Inode!;
        inode.Mode = 0x180; // 0600
        inode.Uid = 0;

        var fd = env.Invoke("SysInotifyInit1", 0);
        Assert.True(fd >= 0);
        env.PutString(0x10000, "/private");

        env.Process.UID = env.Process.EUID = env.Process.FSUID = 1000;
        Assert.Equal(-(int)Errno.EACCES, env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000,
            LinuxConstants.IN_ATTRIB));

        env.Process.UID = env.Process.EUID = env.Process.FSUID = 0;
        Assert.True(env.Invoke("SysInotifyAddWatch", (uint)fd, 0x10000, LinuxConstants.IN_ATTRIB) > 0);
        env.Sm.FreeFD(fd);
    }

    [Fact]
    public void ThpDisable_RoundTrips()
    {
        using var env = new SyscallTestEnv();

        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_SET_THP_DISABLE, 1));
        Assert.Equal(1, env.Invoke("SysPrctl", LinuxConstants.PR_GET_THP_DISABLE));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_SET_THP_DISABLE, 0));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_GET_THP_DISABLE));
    }

    [Fact]
    public void ClockNanosleep_DistinguishesInvalidAndUnsupportedClocks()
    {
        using var env = new SyscallTestEnv();

        Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysClockNanosleep", 99));
        Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysClockNanosleep", unchecked((uint)-1)));
        Assert.Equal(-(int)Errno.EOPNOTSUPP,
            env.Invoke("SysClockNanosleep", LinuxConstants.CLOCK_THREAD_CPUTIME_ID));
        Assert.Equal(-(int)Errno.EOPNOTSUPP,
            env.Invoke("SysClockNanosleep", LinuxConstants.CLOCK_MONOTONIC_RAW));
        Assert.Equal(-(int)Errno.EPERM, env.Invoke("SysClockNanosleep", LinuxConstants.CLOCK_REALTIME_ALARM));
    }
}
