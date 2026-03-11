using System.Reflection;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

public class WaitStatusEncodingTests
{
    private static int InvokePrivateInt(string name, params object[] args)
    {
        var method = typeof(SyscallManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (int)method!.Invoke(null, args)!;
    }

    private static SigInfo InvokeBuildSigInfo(Process proc)
    {
        var method = typeof(SyscallManager).GetMethod("BuildSigchldInfoForExit",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (SigInfo)method!.Invoke(null, [proc])!;
    }

    [Fact]
    public void EncodeWaitExitStatus_NormalExit_UsesHighByte()
    {
        var child = new Process(2000, null!, null!)
        {
            ExitedBySignal = false,
            ExitStatus = 42
        };

        var status = InvokePrivateInt("EncodeWaitExitStatus", child);
        Assert.Equal(42 << 8, status);
    }

    [Fact]
    public void EncodeWaitExitStatus_Signaled_UsesSignalBits()
    {
        var child = new Process(2001, null!, null!)
        {
            ExitedBySignal = true,
            TermSignal = (int)Signal.SIGTERM,
            CoreDumped = false
        };

        var status = InvokePrivateInt("EncodeWaitExitStatus", child);
        Assert.Equal((int)Signal.SIGTERM, status);
    }

    [Fact]
    public void EncodeWaitExitStatus_Coredump_SetsCoreBit()
    {
        var child = new Process(2002, null!, null!)
        {
            ExitedBySignal = true,
            TermSignal = (int)Signal.SIGSEGV,
            CoreDumped = true
        };

        var status = InvokePrivateInt("EncodeWaitExitStatus", child);
        Assert.Equal(((int)Signal.SIGSEGV & 0x7f) | 0x80, status);
    }

    [Fact]
    public void EncodeWaitStoppedStatus_UsesStoppedLayout()
    {
        var status = InvokePrivateInt("EncodeWaitStoppedStatus", (int)Signal.SIGTSTP);
        Assert.Equal(0x7f | ((int)Signal.SIGTSTP << 8), status);
    }

    [Fact]
    public void BuildSigchldInfoForExit_UsesKilledCodeForSignaledExit()
    {
        var child = new Process(2003, null!, null!)
        {
            ExitedBySignal = true,
            TermSignal = (int)Signal.SIGKILL,
            CoreDumped = false
        };

        var info = InvokeBuildSigInfo(child);
        Assert.Equal((int)Signal.SIGCHLD, info.Signo);
        Assert.Equal(2, info.Code); // CLD_KILLED
        Assert.Equal((int)Signal.SIGKILL, info.Status);
    }
}