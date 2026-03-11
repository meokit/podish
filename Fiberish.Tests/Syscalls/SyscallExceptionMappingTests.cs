using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SyscallExceptionMappingTests
{
    [Fact]
    public void MapSyscallExceptionToErrno_OutOfMemoryException_ReturnsEnomem()
    {
        var rc = SyscallManager.MapSyscallExceptionToErrno(new OutOfMemoryException("oom"));
        Assert.Equal(-(int)Errno.ENOMEM, rc);
    }

    [Fact]
    public void MapSyscallExceptionToErrno_UnknownException_ReturnsEfault()
    {
        var rc = SyscallManager.MapSyscallExceptionToErrno(new Exception("boom"));
        Assert.Equal(-(int)Errno.EFAULT, rc);
    }
}