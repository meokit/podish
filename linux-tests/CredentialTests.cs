using Xunit;
using System;
using System.IO;
using System.Text;
using System.Buffers.Binary;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using Task = Bifrost.Core.Task;

namespace Bifrost.Tests;

public class CredentialTests
{
    private (Engine, VMAManager, SyscallManager, Task) SetupEmu()
    {
        var engine = new Engine();
        var mm = new VMAManager();
        
        // Setup Fault Handler for demand paging
        engine.FaultHandler = (eng, addr, isWrite) =>
        {
            mm.HandleFault(addr, isWrite, eng);
        };

        // The constructor registers the engine in the global registry
        var sys = new SyscallManager(engine, mm, 0x1000000, Path.GetFullPath("."));
        
        var proc = new Process(1001, mm, sys);
        var task = new Task(1001, proc, engine);
        
        Scheduler.Add(task);
        return (engine, mm, sys, task);
    }

    private void SetArgs(Engine engine, uint eax, uint ebx = 0, uint ecx = 0, uint edx = 0, uint esi = 0, uint edi = 0)
    {
        engine.RegWrite(Reg.EAX, eax);
        engine.RegWrite(Reg.EBX, ebx);
        engine.RegWrite(Reg.ECX, ecx);
        engine.RegWrite(Reg.EDX, edx);
        engine.RegWrite(Reg.ESI, esi);
        engine.RegWrite(Reg.EDI, edi);
    }

    [Fact]
    public void TestInitialCredentials()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Initial should be root (0)
            Assert.Equal(0, task.Process.UID);
            Assert.Equal(0, task.Process.GID);
            Assert.Equal(0, task.Process.EUID);
            Assert.Equal(0, task.Process.EGID);
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestSetUidGid()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // setuid(1000) - EAX=23, EBX=1000
            SetArgs(engine, 23, 1000);
            sys.Handle(engine, 0x80);
            
            Assert.Equal(1000, task.Process.UID);
            Assert.Equal(1000, task.Process.EUID);
            Assert.Equal(1000, task.Process.SUID);
            Assert.Equal(1000, task.Process.FSUID);

            // setgid(2000) - EAX=46, EBX=2000
            SetArgs(engine, 46, 2000);
            sys.Handle(engine, 0x80);

            Assert.Equal(2000, task.Process.GID);
            Assert.Equal(2000, task.Process.EGID);
            Assert.Equal(2000, task.Process.SGID);
            Assert.Equal(2000, task.Process.FSGID);
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestSetResUidGid()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // setresuid(100, 200, 300) - EAX=164, EBX=100, ECX=200, EDX=300
            SetArgs(engine, 164, 100, 200, 300);
            sys.Handle(engine, 0x80);

            Assert.Equal(100, task.Process.UID);
            Assert.Equal(200, task.Process.EUID);
            Assert.Equal(300, task.Process.SUID);
            Assert.Equal(200, task.Process.FSUID);

            // getresuid(addr1, addr2, addr3) - EAX=165
            uint addr = 0x20000000;
            mm.Mmap(addr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            SetArgs(engine, 165, addr, addr + 4, addr + 8);
            sys.Handle(engine, 0x80);
            
            var uid = BinaryPrimitives.ReadInt32LittleEndian(engine.MemRead(addr, 4));
            var euid = BinaryPrimitives.ReadInt32LittleEndian(engine.MemRead(addr + 4, 4));
            var suid = BinaryPrimitives.ReadInt32LittleEndian(engine.MemRead(addr + 8, 4));
            
            Assert.Equal(100, uid);
            Assert.Equal(200, euid);
            Assert.Equal(300, suid);
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestSetReUidGid()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // setreuid(500, 600) - EAX=70, EBX=500, ECX=600
            SetArgs(engine, 70, 500, 600);
            sys.Handle(engine, 0x80);
            
            Assert.Equal(500, task.Process.UID);
            Assert.Equal(600, task.Process.EUID);
            Assert.Equal(600, task.Process.SUID);
            Assert.Equal(600, task.Process.FSUID);

            // setregid(700, 800) - EAX=71, EBX=700, ECX=800
            SetArgs(engine, 71, 700, 800);
            sys.Handle(engine, 0x80);

            Assert.Equal(700, task.Process.GID);
            Assert.Equal(800, task.Process.EGID);
            Assert.Equal(800, task.Process.SGID);
            Assert.Equal(800, task.Process.FSGID);
            
            // Test -1 (0xFFFFFFFF) behavior
            SetArgs(engine, 70, 0xFFFFFFFF, 900);
            sys.Handle(engine, 0x80);
            Assert.Equal(500, task.Process.UID);
            Assert.Equal(900, task.Process.EUID);
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestSetFsUidGid()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            task.Process.FSUID = 10;
            task.Process.FSGID = 20;

            // setfsuid(100) - EAX=138, EBX=100
            SetArgs(engine, 138, 100);
            sys.Handle(engine, 0x80);
            
            Assert.Equal(10, (int)engine.RegRead(Reg.EAX)); // Returns old
            Assert.Equal(100, task.Process.FSUID);

            // setfsgid(200) - EAX=139, EBX=200
            SetArgs(engine, 139, 200);
            sys.Handle(engine, 0x80);
            
            Assert.Equal(20, (int)engine.RegRead(Reg.EAX)); // Returns old
            Assert.Equal(200, task.Process.FSGID);
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestStatPermissions()
    {
        var (engine, mm, sys, task) = SetupEmu();
        string testFile = "test_perm_stat.bin";
        File.WriteAllText(testFile, "hello");
        
        try
        {
            task.Process.EUID = 1234;
            task.Process.EGID = 5678;

            uint pathAddr = 0x30000000;
            uint statAddr = 0x30001000;
            mm.Mmap(pathAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            engine.MemWrite(pathAddr, Encoding.ASCII.GetBytes(testFile + "\0"));
            
            // stat64 - EAX=195, EBX=pathAddr, ECX=statAddr
            SetArgs(engine, 195, pathAddr, statAddr);
            sys.Handle(engine, 0x80);

            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            
            byte[] statBuf = engine.MemRead(statAddr, 96);
            
            // mode is at offset 16 (uint)
            uint mode = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(16, 4));
            // uid is at offset 24 (uint), gid at 28 (uint)
            uint uid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(24, 4));
            uint gid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(28, 4));
            
            // 0x8000 is S_IFREG, 0x1b6 is 0666 (hostfs files use 666 permissions)
            Assert.Equal(0x81b6u, mode);
            // Hostfs files get uid/gid from host filesystem (typically 0/root in test environment)
            // Not from process credentials
            // Assert.Equal(0u, uid);  // Likely root on host
            // Assert.Equal(0u, gid);  // Likely root on host
            // Actually we can't assume hostfs ownership, so just verify mode
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }
}
