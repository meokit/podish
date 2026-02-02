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

public class VFSAttributeTests
{
    private (Engine, VMAManager, SyscallManager, Task) SetupEmu()
    {
        var engine = new Engine();
        var mm = new VMAManager();
        
        engine.FaultHandler = (eng, addr, isWrite) =>
        {
            mm.HandleFault(addr, isWrite, eng);
        };

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
    public void TestTmpfsFileOwnership()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Set process credentials to non-root
            task.Process.EUID = 1234;
            task.Process.EGID = 5678;

            // Mount tmpfs
            Directory.CreateDirectory("test_mount");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_mount\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Create a file in tmpfs
            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_mount/test.txt\0"));
            
            // open(path, O_CREAT | O_RDWR, 0644)
            SetArgs(engine, 5, fileAddr, 0x42, 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // Stat the file to check ownership
            uint statAddr = 0x20003000;
            mm.Mmap(statAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(statAddr, true, engine);
            
            SetArgs(engine, 195, fileAddr, statAddr);  // stat64
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            
            byte[] statBuf = engine.MemRead(statAddr, 96);
            uint uid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(24, 4));
            uint gid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(28, 4));
            
            // File should be owned by process EUID/EGID
            Assert.Equal(1234u, uid);
            Assert.Equal(5678u, gid);

            // Close FD
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_mount")) Directory.Delete("test_mount", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestChmod()
    {
        var (engine, mm, sys, task) = SetupEmu();
       try
        {
            // Mount tmpfs and create file
            Directory.CreateDirectory("test_chmod");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_chmod\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);

            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_chmod/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644);  // open O_CREAT
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // chmod to 0777 (octal)
            SetArgs(engine, 15, fileAddr, 0x1ff);  // chmod(path, 0x1ff = 0777 octal)
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Verify mode changed
            uint statAddr = 0x20003000;
            mm.Mmap(statAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(statAddr, true, engine);
            
            SetArgs(engine, 195, fileAddr, statAddr);  // stat64
            sys.Handle(engine, 0x80);
            
            byte[] statBuf = engine.MemRead(statAddr, 96);
            uint mode = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(16, 4));
            
            // Should have 0x1ff (0777 octal) permissions (plus S_IFREG)
            Assert.Equal(0x81ffu, mode & 0x8FFF);

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_chmod")) Directory.Delete("test_chmod", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestChmodPermissionDenied()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Create file as root
            task.Process.EUID = 0;
            
            Directory.CreateDirectory("test_perm");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_perm\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);

            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_perm/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Switch to non-owner user
            task.Process.EUID = 1234;

            // Try chmod - should fail with EPERM
            SetArgs(engine, 15, fileAddr, 0777);
            sys.Handle(engine, 0x80);
            Assert.Equal(-1, (int)engine.RegRead(Reg.EAX));  // EPERM
        }
        finally
        {
            if (Directory.Exists("test_perm")) Directory.Delete("test_perm", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestChown()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Must be root to chown
            task.Process.EUID = 0;
            
            Directory.CreateDirectory("test_chown");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_chown\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);

            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_chown/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // chown(path, 9999, 8888)
            SetArgs(engine, 16, fileAddr, 9999, 8888);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Verify ownership changed
            uint statAddr = 0x20003000;
            mm.Mmap(statAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(statAddr, true, engine);
            
            SetArgs(engine, 195, fileAddr, statAddr);
            sys.Handle(engine, 0x80);
            
            byte[] statBuf = engine.MemRead(statAddr, 96);
            uint uid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(24, 4));
            uint gid = BinaryPrimitives.ReadUInt32LittleEndian(statBuf.AsSpan(28, 4));
            
            Assert.Equal(9999u, uid);
            Assert.Equal(8888u, gid);

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_chown")) Directory.Delete("test_chown", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestChownNonRoot()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            task.Process.EUID = 1234;  // Non-root
            
            Directory.CreateDirectory("test_chown_fail");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_chown_fail\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);

            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_chown_fail/file.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // Try chown as non-root - should fail
            SetArgs(engine, 16, fileAddr, 5555, 6666);
            sys.Handle(engine, 0x80);
            Assert.Equal(-1, (int)engine.RegRead(Reg.EAX));  // EPERM

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_chown_fail")) Directory.Delete("test_chown_fail", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }
}
