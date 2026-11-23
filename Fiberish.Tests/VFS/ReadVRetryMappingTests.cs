using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public sealed class ReadVRetryMappingTests
{
    [Theory]
    [InlineData("remap")]
    [InlineData("unmap")]
    [InlineData("readonly")]
    public async Task ReadV_AfterAwaitResolvesCurrentMappingInsteadOfReusingOldPointer(string change)
    {
        using var context = new MemoryRuntimeContext();
        using var engine = new Engine(context);
        var mm = new VMAManager(context);
        var syscalls = new SyscallManager(engine, mm, 0);
        var process = new Process(100, mm, syscalls);
        var scheduler = new KernelScheduler();
        scheduler.RegisterProcess(process);
        var task = new FiberTask(100, process, engine, scheduler);
        engine.PageFaultResolver = null;
        const uint address = 0x48000000;
        IntPtr oldPage = Marshal.AllocHGlobal(4096);
        IntPtr newPage = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.Copy(new byte[4096], 0, oldPage, 4096);
            Marshal.Copy(new byte[4096], 0, newPage, 4096);
            Assert.True(engine.MemMapHostPage(address, oldPage, 3));
            var inode = new RetryInode { SuperBlock = syscalls.MemfdSuperBlock };
            var dentry = new Dentry(FsName.FromString("retry"), inode, null, inode.SuperBlock);
            var file = new LinuxFile(dentry, FileFlags.O_RDONLY, syscalls.AnonMount);
            try
            {
                var read = inode.ReadV(engine, file, task,
                    new ArraySegment<Iovec>([new Iovec { BaseAddr = address, Len = 1 }]), 0, 0);
                Assert.False(read.IsCompleted);
                Assert.Equal(1, inode.ReadAttempts);
                if (change == "remap") Assert.True(engine.MemMapHostPage(address, newPage, 3));
                else if (change == "unmap") engine.MemUnmap(address, 4096);
                else engine.MemProtect(address, 4096, 1);
                inode.Ready.SetResult(AwaitResult.Completed);

                Assert.Equal(change == "remap" ? 1 : -(int)Errno.EFAULT, await read);
                Assert.Equal((byte)0, Marshal.ReadByte(oldPage));
                Assert.Equal(change == "remap" ? (byte)0x5A : (byte)0, Marshal.ReadByte(newPage));
            }
            finally { file.Dispose(); }
        }
        finally
        {
            engine.MemUnmap(address, 4096);
            syscalls.Close();
            Marshal.FreeHGlobal(oldPage);
            Marshal.FreeHGlobal(newPage);
        }
    }

    private sealed class RetryInode : Inode
    {
        public TaskCompletionSource<AwaitResult> Ready { get; } = new();
        public int ReadAttempts { get; private set; }
        public override int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset)
        {
            if (++ReadAttempts == 1) return -(int)Errno.EAGAIN;
            buffer[0] = 0x5A;
            return 1;
        }
        public override ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task) => new(Ready.Task);
    }
}
