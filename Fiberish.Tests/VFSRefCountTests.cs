using Xunit;
using System;
using System.IO;
using System.Text;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using Task = Bifrost.Core.Task;

namespace Bifrost.Tests;

public class VFSRefCountTests
{
    private (Engine, VMAManager, SyscallManager, Task) SetupEmu()
    {
        var engine = new Engine();
        var mm = new VMAManager();
        
        engine.FaultHandler = (eng, addr, isWrite) => mm.HandleFault(addr, isWrite, eng);

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
    public void TestUmountBusy()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            Directory.CreateDirectory("test_busy");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_busy\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Create and open a file
            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_busy/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644); // open O_CREAT
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0, "File creation should succeed");

            // Try to umount - should fail with EBUSY
            SetArgs(engine, 22, mountAddr); // umount
            sys.Handle(engine, 0x80);
            Assert.Equal(-16, (int)engine.RegRead(Reg.EAX)); // EBUSY

            // Close the file
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Now umount should succeed
            SetArgs(engine, 22, mountAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            if (Directory.Exists("test_busy")) Directory.Delete("test_busy", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestLazyUmount()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            Directory.CreateDirectory("test_lazy");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_lazy\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Create and open a file
            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_lazy/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // Lazy umount2 with MNT_DETACH (flags=2) - should succeed
            SetArgs(engine, 52, mountAddr, 2); // umount2 with MNT_DETACH
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // File operations should still work
            uint writeAddr = 0x20003000;
            mm.Mmap(writeAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(writeAddr, true, engine);
            engine.MemWrite(writeAddr, Encoding.ASCII.GetBytes("test data"));
            
            SetArgs(engine, 4, (uint)fd, writeAddr, 9); // write
            sys.Handle(engine, 0x80);
            Assert.Equal(9, (int)engine.RegRead(Reg.EAX));

            // Close the file - this should trigger cleanup
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            if (Directory.Exists("test_lazy")) Directory.Delete("test_lazy", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }
}
