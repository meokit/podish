using Xunit;
using System;
using System.IO;
using System.Text;
using System.Buffers.Binary;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using Bifrost.VFS;
using Task = Bifrost.Core.Task;

namespace Bifrost.Tests;

public class StatxTests
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
    public void TestStatxBasic()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            Directory.CreateDirectory("test_statx");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_statx\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0); // mount
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Create a file
            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_statx/test.txt\0"));
            
            SetArgs(engine, 5, fileAddr, 0x42, 0x1A4); // open(O_CREAT)
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // Write some data to set size
            byte[] data = Encoding.ASCII.GetBytes("Hello Statx");
            uint dataAddr = 0x20003000;
            mm.Mmap(dataAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(dataAddr, true, engine);
            engine.MemWrite(dataAddr, data);
            SetArgs(engine, 4, (uint)fd, dataAddr, (uint)data.Length); // write
            sys.Handle(engine, 0x80);
            Assert.Equal(data.Length, (int)engine.RegRead(Reg.EAX));

            // Statx
            uint statxAddr = 0x20004000;
            mm.Mmap(statxAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(statxAddr, true, engine);
            
            // statx(dirfd, path, flags, mask, buf) - syscall 383
            SetArgs(engine, 383, (uint)LinuxConstants.AT_FDCWD, fileAddr, 0, (uint)LinuxConstants.STATX_BASIC_STATS, statxAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            byte[] statxBuf = engine.MemRead(statxAddr, 256);
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(statxBuf.AsSpan(0x00, 4));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(statxBuf.AsSpan(0x28, 8));
            ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(statxBuf.AsSpan(0x1C, 2));

            Assert.Equal(LinuxConstants.STATX_BASIC_STATS, mask);
            Assert.Equal((ulong)data.Length, size);
            Assert.Equal((ushort)(0x81A4), mode); // S_IFREG | 0644

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_statx")) Directory.Delete("test_statx", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestStatxEmptyPath()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            Directory.CreateDirectory("test_statx_empty");
            uint mountAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mountAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(mountAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mountAddr, Encoding.ASCII.GetBytes("/test_statx_empty\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            SetArgs(engine, 21, 0, mountAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);

            // Create file
            uint fileAddr = 0x20002000;
            mm.Mmap(fileAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileAddr, true, engine);
            engine.MemWrite(fileAddr, Encoding.ASCII.GetBytes("/test_statx_empty/emptyfile\0"));
            SetArgs(engine, 5, fileAddr, 0x42, 0x1A4);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);

            // Statx with AT_EMPTY_PATH
            uint statxAddr = 0x20003000;
            uint emptyPathAddr = 0x20004000;
            mm.Mmap(statxAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(statxAddr, true, engine);
            mm.HandleFault(emptyPathAddr, true, engine);
            engine.MemWrite(emptyPathAddr, new byte[] { 0 }); // ""

            SetArgs(engine, 383, (uint)fd, emptyPathAddr, (uint)LinuxConstants.AT_EMPTY_PATH, (uint)LinuxConstants.STATX_BASIC_STATS, statxAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            byte[] statxBuf = engine.MemRead(statxAddr, 256);
            ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(statxBuf.AsSpan(0x1C, 2));
            Assert.Equal(0x81A4u, (uint)mode);

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (Directory.Exists("test_statx_empty")) Directory.Delete("test_statx_empty", true);
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestClockAndUid32()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            task.Process.UID = 1001;
            // 1. Test getuid32 (syscall 199)
            SetArgs(engine, 199);
            sys.Handle(engine, 0x80);
            Assert.Equal(1001, (int)engine.RegRead(Reg.EAX));

            // 2. Test clock_gettime (syscall 265)
            uint tsPtr = 0x20000000;
            mm.Mmap(tsPtr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(tsPtr, true, engine);

            SetArgs(engine, 265, (uint)LinuxConstants.CLOCK_REALTIME, tsPtr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            byte[] tsBuf = engine.MemRead(tsPtr, 8);
            int secs = BitConverter.ToInt32(tsBuf, 0);
            Assert.True(secs > 1700000000); // Check for reasonable unix timestamp
        }
        finally
        {
            sys.Close();
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }
}
