#if FIBER_GUEST_AARCH64
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.Tests.Syscalls;
using Xunit;

namespace Fiberish.Tests.Memory;

public class AArch64AddressSpaceTests
{
    [Fact]
    public void SparseReservationProtectsAndUnmapsHighPagesWithoutAliasingLowAddresses()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var mem = env.Mem;
        var engine = env.Engine;
        const ulong length = 10UL << 30;
        const ulong low = 0x10000;

        var initialPageCount = mem.PageMapping.Count;
        var start = ProcessAddressSpaceSync.Mmap(mem, engine, 0, length, Protection.None,
            MapFlags.Private | MapFlags.Anonymous, null, 0, "[reservation]");
        Assert.InRange(start, 1UL << 32, LinuxConstants.TaskSizeAArch64 - length);
        var page = ((start + uint.MaxValue) & ~((ulong)uint.MaxValue)) + low;
        Assert.Equal(initialPageCount, mem.PageMapping.Count);
        Assert.Equal(start + length, mem.FindVmArea(page)!.End);
        var lastPage = start + length - LinuxConstants.PageSize;
        Assert.Throws<OutOfMemoryException>(() => ProcessAddressSpaceSync.Mmap(mem, engine,
            lastPage, LinuxConstants.TaskSizeAArch64 - lastPage + LinuxConstants.PageSize, Protection.Read,
            MapFlags.Fixed | MapFlags.Private | MapFlags.Anonymous, null, 0, "[invalid reservation]"));
        Assert.Equal(start + length, mem.FindVmArea(lastPage)!.End);

        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(mem, engine, page, LinuxConstants.PageSize,
            Protection.Read | Protection.Write));
        Assert.Equal(FaultResult.Handled, mem.HandleFaultDetailed(page, true, engine));
        Assert.True(engine.CopyToUser(low, [0x16]));
        Assert.True(engine.CopyToUser(page, [0x39]));
        Span<byte> data = stackalloc byte[1];
        Assert.True(engine.CopyFromUser(low, data));
        Assert.Equal(0x16, data[0]);
        Assert.True(engine.CopyFromUser(page, data));
        Assert.Equal(0x39, data[0]);
        Assert.Equal(initialPageCount + 1, mem.PageMapping.Count);
        var pagemapOffset = (long)(page / LinuxConstants.PageSize) * sizeof(ulong);
        Assert.Equal(1UL << 63, BinaryPrimitives.ReadUInt64LittleEndian(
            ProcFsManager.ReadPagemap(env.Process, pagemapOffset, sizeof(ulong))));
        Assert.Contains($"{page:x}-{page + LinuxConstants.PageSize:x}", ProcFsManager.GenerateMaps(env.Process));
        Assert.Equal(0, ProcessAddressSpaceSync.Mprotect(mem, engine, page, LinuxConstants.PageSize, Protection.Read));
        Assert.False(engine.CopyToUser(page, [0x40]));
        Assert.True(engine.CopyFromUser(page, data));
        Assert.Equal(0x39, data[0]);

        Assert.Equal(0, ProcessAddressSpaceSync.Munmap(mem, engine, start, length));
        Assert.Null(mem.FindVmArea(page));
        Assert.Equal(initialPageCount, mem.PageMapping.Count);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(
            ProcFsManager.ReadPagemap(env.Process, pagemapOffset, sizeof(ulong))));
        Assert.True(engine.CopyFromUser(low, data));
        Assert.Equal(0x16, data[0]);
    }

    [Fact]
    public void SysVShmRoundsDownAndDetachesHighAddresses()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        const ulong address = (1UL << 37) + 0x3000;
        var shmid = (ulong)Invoke(env, 194, LinuxConstants.IPC_PRIVATE, LinuxConstants.PageSize, 0x1b6);
        Assert.InRange(shmid, 1UL, (ulong)int.MaxValue);
        Assert.Equal((long)address, Invoke(env, 196, shmid, address + 123, LinuxConstants.SHM_RND));
        Assert.Equal(address, env.Mem.FindVmArea(address)!.Start);
        Assert.Equal(FaultResult.Handled, env.Mem.HandleFaultDetailed(address, true, env.Engine));
        Assert.Equal(0, Invoke(env, 195, shmid, LinuxConstants.IPC_STAT, address));
        Span<byte> data = stackalloc byte[8];
        Assert.True(env.Engine.CopyFromUser(address + 48, data));
        Assert.Equal((ulong)LinuxConstants.PageSize, BinaryPrimitives.ReadUInt64LittleEndian(data));
        Assert.Equal(0, Invoke(env, 197, address));
        Assert.Null(env.Mem.FindVmArea(address));
        Assert.Equal(0, Invoke(env, 195, shmid, LinuxConstants.IPC_RMID));
    }

    [Fact]
    public void SysVSemReadsAndWritesHighUserBuffers()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        const ulong address = (1UL << 36) + 0x8000;
        env.Mem.Mmap(address, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Fixed | MapFlags.Private | MapFlags.Anonymous, null, 0, "[ipc]", env.Engine);
        Assert.Equal(FaultResult.Handled, env.Mem.HandleFaultDetailed(address, true, env.Engine));

        var semid = (ulong)Invoke(env, 190, LinuxConstants.IPC_PRIVATE, 1, 0x1b6);
        Assert.InRange(semid, 0UL, (ulong)int.MaxValue);
        Assert.Equal(0, Invoke(env, 191, semid, 0, LinuxConstants.SETVAL, 2));
        Assert.True(env.Engine.CopyToUser(address, [0, 0, 0xff, 0xff, 0, 0])); // sem_op = -1
        Assert.True(env.Engine.CopyToUser(address + 32, new byte[16])); // nonblocking timeout
        Assert.Equal(0, Invoke(env, 192, semid, address, 1, address + 32));
        Assert.Equal(1, Invoke(env, 191, semid, 0, LinuxConstants.GETVAL));
        Assert.Equal(0, Invoke(env, 191, semid, 0, LinuxConstants.IPC_STAT, address));
        Assert.Equal(0, Invoke(env, 191, semid, 0, LinuxConstants.IPC_RMID));
    }

    [Fact]
    public void FileStatAndAccessSyscallsAcceptHighPathsAndResultBuffers()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighUserPage(env);
        var stat = page + 0x400;
        const ulong atFdcwd = LinuxConstants.AT_FDCWD;
        WriteUserBytes(env, page, "/wide-file\0"u8);
        WriteUserBytes(env, page + 0x200, "sample"u8);

        var fd = (ulong)Invoke(env, 56, atFdcwd, page, 0x42, 0x1a4); // O_CREAT | O_RDWR
        Assert.InRange(fd, 0UL, (ulong)int.MaxValue);
        Assert.Equal(6, Invoke(env, 64, fd, page + 0x200, 6));
        Assert.Equal(0, Invoke(env, 80, fd, stat));
        Assert.Equal(6L, ReadInt64(env, stat + 48));
        Assert.Equal(0, Invoke(env, 79, atFdcwd, page, stat, 0));
        Assert.Equal(6L, ReadInt64(env, stat + 48));
        Assert.Equal(0, Invoke(env, 291, atFdcwd, page, 0, LinuxConstants.STATX_BASIC_STATS, stat));
        Assert.Equal(6L, ReadInt64(env, stat + 40));
        Assert.Equal(0, Invoke(env, 48, atFdcwd, page, 0, ulong.MaxValue)); // unused x3
        Assert.Equal(0, Invoke(env, 439, atFdcwd, page, 0, LinuxConstants.AT_SYMLINK_NOFOLLOW));
        Assert.Equal(0, Invoke(env, 43, page, stat));
        Assert.Equal((long)LinuxConstants.PageSize, ReadInt64(env, stat + 8));
        Assert.Equal(0, Invoke(env, 44, fd, stat));
        Assert.Equal((long)LinuxConstants.PageSize, ReadInt64(env, stat + 8));
    }

    [Fact]
    public void DirectorySyscallsAcceptHighStringsAndDirents()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighUserPage(env);
        var link = page + 0x100;
        var target = page + 0x200;
        var dir = page + 0x300;
        var buffer = page + 0x400;
        const ulong atFdcwd = LinuxConstants.AT_FDCWD;
        WriteUserBytes(env, page, "/wide-dir\0"u8);
        WriteUserBytes(env, link, "wide-link\0"u8);
        WriteUserBytes(env, target, "..\0"u8);
        WriteUserBytes(env, dir, ".\0"u8);

        Assert.Equal(0, Invoke(env, 34, atFdcwd, page, 0x1ed));
        Assert.Equal(0, Invoke(env, 49, page));
        Assert.Equal(10, Invoke(env, 17, buffer, 1UL << 32));
        Assert.True(ReadUserBytes(env, buffer, 10).AsSpan().SequenceEqual("/wide-dir\0"u8));
        Assert.Equal(0, Invoke(env, 36, target, atFdcwd, link));
        Assert.Equal(2, Invoke(env, 78, atFdcwd, link, buffer, 16));
        Assert.Equal(".."u8.ToArray(), ReadUserBytes(env, buffer, 2));

        var fd = (ulong)Invoke(env, 56, atFdcwd, dir, 0x4000, 0); // O_DIRECTORY
        Assert.InRange(fd, 0UL, (ulong)int.MaxValue);
        var count = (int)Invoke(env, 61, fd, buffer, 512);
        Assert.InRange(count, 1, 512);
        Assert.True(ReadUserBytes(env, buffer, count).AsSpan().IndexOf("wide-link\0"u8) >= 0);
        Assert.Equal(0, Invoke(env, 35, atFdcwd, link, 0));
        Assert.Equal(-(long)Errno.ENOENT, Invoke(env, 78, atFdcwd, link, buffer, 16));
    }

    [Fact]
    public void PipeAndPollUseUserBuffersAtFourGiB()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        const ulong page = 1UL << 32;
        Assert.Equal((long)page, Invoke(env, 222, page, LinuxConstants.PageSize, 3, 0x32, ulong.MaxValue, 0));
        Assert.Equal(FaultResult.Handled, env.Mem.HandleFaultDetailed(page, true, env.Engine));

        Assert.Equal(0, Invoke(env, 59, page, 0));
        var readFd = BinaryPrimitives.ReadUInt32LittleEndian(ReadUserBytes(env, page, 4));
        var writeFd = BinaryPrimitives.ReadUInt32LittleEndian(ReadUserBytes(env, page + 4, 4));
        WriteUserBytes(env, page + 16, "pipe"u8);
        Assert.Equal(4, Invoke(env, 64, writeFd, page + 16, 4));

        Span<byte> pollfd = stackalloc byte[8];
        pollfd.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(pollfd, readFd);
        BinaryPrimitives.WriteUInt16LittleEndian(pollfd[4..], 1); // POLLIN
        WriteUserBytes(env, page + 32, pollfd);
        WriteUserBytes(env, page + 48, new byte[16]); // Zero timeout
        Assert.Equal(1, Invoke(env, 73, page + 32, 1, page + 48, 0, 0));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ReadUserBytes(env, page + 38, 2)));

        Assert.Equal(0, Invoke(env, 29, readFd, 0x541b, page + 64)); // FIONREAD
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(ReadUserBytes(env, page + 64, 4)));
        Assert.Equal(4, Invoke(env, 63, readFd, page + 80, 4));
        Assert.Equal("pipe"u8.ToArray(), ReadUserBytes(env, page + 80, 4));
        Assert.Equal(-(long)Errno.EFAULT, Invoke(env, 59, LinuxConstants.TaskSizeAArch64, 0));
    }

    [Fact]
    public void SleepSyscallsReadHighTimespec()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighUserPage(env);
        Span<byte> request = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(request, 0);
        BinaryPrimitives.WriteInt64LittleEndian(request[8..], 1_000_000_000);
        WriteUserBytes(env, page, request);

        Assert.Equal(-(long)Errno.EINVAL, Invoke(env, 101, page, 0));
        Assert.Equal(-(long)Errno.EINVAL, Invoke(env, 115, LinuxConstants.CLOCK_MONOTONIC, 0, page, 0));
    }

    [Fact]
    public void SocketMessagesUseHighHeadersVectorsAndPayloads()
    {
        using var env = new SyscallTestEnv(architecture: GuestArchitecture.AArch64);
        var page = MapHighUserPage(env);
        Assert.Equal(0, Invoke(env, 199, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, page));
        var sendFd = BinaryPrimitives.ReadUInt32LittleEndian(ReadUserBytes(env, page, 4));
        var recvFd = BinaryPrimitives.ReadUInt32LittleEndian(ReadUserBytes(env, page + 4, 4));
        var headerAddress = page + 0x100;
        var vectorAddress = page + 0x200;
        var sendAddress = page + 0x300;
        var recvAddress = page + 0x400;
        Span<byte> header = stackalloc byte[56];
        header.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], vectorAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
        WriteUserBytes(env, headerAddress, header);

        Span<byte> vector = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(vector, sendAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(vector[8..], 4);
        WriteUserBytes(env, vectorAddress, vector);
        WriteUserBytes(env, sendAddress, "sock"u8);
        Assert.Equal(4, Invoke(env, 211, sendFd, headerAddress, 0));

        BinaryPrimitives.WriteUInt64LittleEndian(vector, recvAddress);
        WriteUserBytes(env, vectorAddress, vector);
        Assert.Equal(4, Invoke(env, 212, recvFd, headerAddress, 0));
        Assert.Equal("sock"u8.ToArray(), ReadUserBytes(env, recvAddress, 4));
    }

    private static ulong MapHighUserPage(SyscallTestEnv env)
    {
        const ulong page = (1UL << 37) + 0x1000;
        env.Mem.Mmap(page, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Fixed | MapFlags.Private | MapFlags.Anonymous, null, 0, "[wide-syscall]", env.Engine);
        Assert.Equal(FaultResult.Handled, env.Mem.HandleFaultDetailed(page, true, env.Engine));
        return page;
    }

    private static void WriteUserBytes(SyscallTestEnv env, ulong address, ReadOnlySpan<byte> data)
        => Assert.True(env.Engine.CopyToUser(address, data));

    private static byte[] ReadUserBytes(SyscallTestEnv env, ulong address, int size)
    {
        var data = new byte[size];
        Assert.True(env.Engine.CopyFromUser(address, data));
        return data;
    }

    private static long ReadInt64(SyscallTestEnv env, ulong address) =>
        BinaryPrimitives.ReadInt64LittleEndian(ReadUserBytes(env, address, 8));

    private static long Invoke(SyscallTestEnv env, uint number, params ulong[] arguments)
    {
        env.Engine.WriteArmRegister(8, number);
        for (var i = 0; i < 6; i++)
            env.Engine.WriteArmRegister(i, i < arguments.Length ? arguments[i] : 0);
        Assert.True(env.Sm.Handle(env.Engine, 0x80));
        Assert.Null(env.Task.PendingSyscall);
        return (long)env.Engine.ReadArmRegister(0);
    }
}
#endif
