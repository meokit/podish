using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class RlimitSyscallTests
{
    [Fact]
    public async Task DispatchSyscall_GetRlimit_IsHandledAndWritesLegacyStackLimit()
    {
        using var env = new TestEnv();
        const uint limitPtr = 0x10000;
        env.MapUserPage(limitPtr);

        var (handled, rc) = await env.Dispatch(X86SyscallNumbers.getrlimit, unchecked((uint)LinuxConstants.RLIMIT_STACK),
            limitPtr);

        Assert.True(handled);
        Assert.Equal(0, rc);
        Assert.Equal(8U * 1024 * 1024, env.ReadUInt32(limitPtr));
        Assert.Equal(LinuxConstants.RLIM32_INFINITY, env.ReadUInt32(limitPtr + 4));
    }

    [Fact]
    public async Task DispatchSyscall_UGetRlimit_IsHandledAndWritesLegacyNofileLimit()
    {
        using var env = new TestEnv();
        const uint limitPtr = 0x11000;
        env.MapUserPage(limitPtr);

        var (handled, rc) = await env.Dispatch(X86SyscallNumbers.ugetrlimit,
            unchecked((uint)LinuxConstants.RLIMIT_NOFILE), limitPtr);

        Assert.True(handled);
        Assert.Equal(0, rc);
        Assert.Equal(1024U, env.ReadUInt32(limitPtr));
        Assert.Equal(4096U, env.ReadUInt32(limitPtr + 4));
    }

    [Fact]
    public async Task SysPrlimit64_UpdatesLimitAndReturnsPreviousValue()
    {
        using var env = new TestEnv();
        const uint newPtr = 0x12000;
        const uint oldPtr = 0x13000;
        env.MapUserPage(newPtr);
        env.MapUserPage(oldPtr);
        env.WriteRlimit64(newPtr, 4UL * 1024 * 1024, 8UL * 1024 * 1024);

        var rc = await env.Invoke("SysPrlimit64", 0, unchecked((uint)LinuxConstants.RLIMIT_STACK), newPtr, oldPtr, 0,
            0);

        Assert.Equal(0, rc);
        Assert.Equal(8UL * 1024 * 1024, env.ReadUInt64(oldPtr));
        Assert.Equal(ulong.MaxValue, env.ReadUInt64(oldPtr + 8));

        const uint checkPtr = 0x14000;
        env.MapUserPage(checkPtr);
        rc = await env.Invoke("SysGetRlimit", unchecked((uint)LinuxConstants.RLIMIT_STACK), checkPtr, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        Assert.Equal(4U * 1024 * 1024, env.ReadUInt32(checkPtr));
        Assert.Equal(8U * 1024 * 1024, env.ReadUInt32(checkPtr + 4));
    }

    [Fact]
    public async Task SysSetRlimit_RejectsNonRootHardLimitRaise()
    {
        using var env = new TestEnv();
        env.Process.EUID = 1000;

        const uint limitPtr = 0x15000;
        env.MapUserPage(limitPtr);
        env.WriteRlimit32(limitPtr, 1024, 8192);

        var rc = await env.Invoke("SysSetRlimit", unchecked((uint)LinuxConstants.RLIMIT_NOFILE), limitPtr, 0, 0, 0, 0);

        Assert.Equal(-(int)Errno.EPERM, rc);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process = new Process(100, Vma, SyscallManager);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
        }

        public async ValueTask<(bool Handled, int Rc)> Dispatch(uint eax, uint ebx = 0, uint ecx = 0, uint edx = 0,
            uint esi = 0, uint edi = 0, uint ebp = 0)
        {
            var method = typeof(SyscallManager).GetMethod("DispatchSyscall",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            object?[] args = [eax, Engine, ebx, ecx, edx, esi, edi, ebp, false];
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, args)!;
            var rc = await task;
            return ((bool)args[8]!, rc);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public uint ReadUInt32(uint addr)
        {
            var buf = new byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadUInt32LittleEndian(buf);
        }

        public ulong ReadUInt64(uint addr)
        {
            var buf = new byte[8];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadUInt64LittleEndian(buf);
        }

        public void WriteRlimit32(uint addr, uint soft, uint hard)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), soft);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), hard);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public void WriteRlimit64(uint addr, ulong soft, ulong hard)
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), soft);
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8), hard);
            Assert.True(Engine.CopyToUser(addr, buf));
        }
    }
}
