using Xunit;
using System;
using System.IO;
using System.Text;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using Bifrost.VFS;

namespace Bifrost.Tests;

public class SyscallBatchTests
{
    private (Engine, VMAManager, SyscallManager, Bifrost.Core.Task) SetupEmu()
    {
        var engine = new Engine();
        var mm = new VMAManager();
        var sys = new SyscallManager(engine, mm, 0x10000000, ".");
        var proc = new Process(1000, mm, sys);
        var task = new Bifrost.Core.Task(1001, proc, engine);
        sys.RegisterEngine(engine);
        Scheduler.Add(task);
        return (engine, mm, sys, task);
    }

    private void SetArgs(Engine engine, uint num, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        engine.RegWrite(Reg.EAX, num);
        engine.RegWrite(Reg.EBX, a1);
        engine.RegWrite(Reg.ECX, a2);
        engine.RegWrite(Reg.EDX, a3);
        engine.RegWrite(Reg.ESI, a4);
        engine.RegWrite(Reg.EDI, a5);
        engine.RegWrite(Reg.EBP, a6);
    }

    [Fact]
    public void TestGettidAndGetpgid()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // gettid (224)
            SetArgs(engine, 224);
            sys.Handle(engine, 0x80);
            Assert.Equal(1001, (int)engine.RegRead(Reg.EAX));

            // getpgid (132)
            SetArgs(engine, 132, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(1000, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestUmask()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // umask (60) - set to 077 (63 decimal)
            SetArgs(engine, 60, 63);
            sys.Handle(engine, 0x80);
            int old = (int)engine.RegRead(Reg.EAX);
            Assert.Equal(18, old); // Default was 022 octal = 18 decimal

            // Create file and check mode
            uint nameAddr = 0x20000000;
            mm.Mmap(nameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(nameAddr, true, engine);
            engine.MemWrite(nameAddr, Encoding.ASCII.GetBytes("test_umask.txt\0"));

            // open with O_CREAT (64)
            SetArgs(engine, 5, nameAddr, 64, 0x1FF); // 0x1FF = 0777
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            var file = sys.GetFD(fd);
            // Mode should be 0777 & ~077 = 0700 (448 decimal)
            Assert.Equal(448, file!.Dentry.Inode!.Mode);

            if (System.IO.File.Exists("test_umask.txt")) System.IO.File.Delete("test_umask.txt");
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
            if (System.IO.File.Exists("test_umask.txt")) System.IO.File.Delete("test_umask.txt");
        }
    }

    [Fact]
    public void TestSyncAndMsync()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // sync (36)
            SetArgs(engine, 36);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // msync (144)
            uint addr = 0x30000000;
            mm.Mmap(addr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            SetArgs(engine, 144, addr, 4096, 1); // MS_ASYNC = 1
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestUTSNamespace()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // sethostname (74)
            uint hostAddr = 0x50000000;
            mm.Mmap(hostAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(hostAddr, true, engine);
            engine.MemWrite(hostAddr, Encoding.ASCII.GetBytes("new-host\0"));
            SetArgs(engine, 74, hostAddr, 8);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            Assert.Equal("new-host", task.Process.UTS.NodeName);

            // setdomainname (121)
            uint domAddr = 0x50001000;
            mm.Mmap(domAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(domAddr, true, engine);
            engine.MemWrite(domAddr, Encoding.ASCII.GetBytes("new-domain\0"));
            SetArgs(engine, 121, domAddr, 10);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            Assert.Equal("new-domain", task.Process.UTS.DomainName);

            // uname (122)
            uint unameAddr = 0x50002000;
            mm.Mmap(unameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(unameAddr, true, engine);
            SetArgs(engine, 122, unameAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            byte[] res = engine.MemRead(unameAddr, 6 * 65);
            string sysname = Encoding.ASCII.GetString(res, 0, 65).TrimEnd('\0');
            string nodename = Encoding.ASCII.GetString(res, 65, 65).TrimEnd('\0');
            string domainname = Encoding.ASCII.GetString(res, 325, 65).TrimEnd('\0');

            Assert.Equal("Linux", sysname);
            Assert.Equal("new-host", nodename);
            Assert.Equal("new-domain", domainname);
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestOpenat2()
    {
        var (engine, mm, sys, task) = SetupEmu();
        string testFile = "test_openat2.txt";
        System.IO.File.WriteAllText(testFile, "hello");
        try
        {
            uint nameAddr = 0x40000000;
            uint howAddr = 0x40001000;
            mm.Mmap(nameAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(nameAddr, true, engine);
            mm.HandleFault(howAddr, true, engine);

            engine.MemWrite(nameAddr, Encoding.ASCII.GetBytes(testFile + "\0"));
            
            // struct open_how: flags=O_RDONLY(0), mode=0, resolve=0
            byte[] how = new byte[24]; 
            engine.MemWrite(howAddr, how);

            // openat2(dirfd=-100, pathname, how, size=24)
            SetArgs(engine, 437, 0xFFFFFF9C, nameAddr, howAddr, 24);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);
            
            sys.FreeFD(fd);
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
            if (System.IO.File.Exists(testFile)) System.IO.File.Delete(testFile);
        }
    }
}
