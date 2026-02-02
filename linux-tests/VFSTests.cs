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

public class VFSTests
{
    private (Engine, VMAManager, SyscallManager, Task) SetupEmu()
    {
        var engine = new Engine();
        var mm = new VMAManager();
        
        // Setup Fault Handler for demand paging
        engine.FaultHandler = (eng, addr, isWrite) =>
        {
            if (!mm.HandleFault(addr, isWrite, eng))
            {
                eng.SetStatusFault();
            }
        };

        // Current directory as host root for the tests
        var sys = new SyscallManager(engine, mm, 0x1000000, Path.GetFullPath("."));
        
        var proc = new Process(Task.NextPID(), mm, sys);
        var task = new Task(proc.TGID, proc, engine);
        
        Scheduler.Add(task);
        return (engine, mm, sys, task);
    }

    private void SetArgs(Engine engine, uint eax, uint ebx = 0, uint ecx = 0, uint edx = 0, uint esi = 0, uint edi = 0, uint ebp = 0)
    {
        engine.RegWrite(Reg.EAX, eax);
        engine.RegWrite(Reg.EBX, ebx);
        engine.RegWrite(Reg.ECX, ecx);
        engine.RegWrite(Reg.EDX, edx);
        engine.RegWrite(Reg.ESI, esi);
        engine.RegWrite(Reg.EDI, edi);
        engine.RegWrite(Reg.EBP, ebp);
    }

    [Fact]
    public void TestMountUmountTmpfs()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            uint fsTypeAddr = 0x20001000;
            uint mountPointAddr = 0x20002000;
            mm.Mmap(fsTypeAddr, 4096 * 2, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);

            string fsType = "tmpfs";
            string mountPoint = "/mnt";
            
            // Create the mount point directory first
            System.IO.Directory.CreateDirectory("mnt");

            // Manually trigger faults to ensure pages are populated before MemWrite
            mm.HandleFault(fsTypeAddr, true, engine);
            mm.HandleFault(mountPointAddr, true, engine);

            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes(fsType + "\0"));
            engine.MemWrite(mountPointAddr, Encoding.ASCII.GetBytes(mountPoint + "\0"));

            // 1. Mount tmpfs
            // mount(source, target, filesystemtype, mountflags, data)
            SetArgs(engine, 21, 0, mountPointAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // 2. Create a file in the mounted tmpfs
            string fileName = "/mnt/test.txt";
            uint fileNameAddr = 0x20003000;
            mm.Mmap(fileNameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(fileNameAddr, true, engine);  // Trigger fault before MemWrite
            engine.MemWrite(fileNameAddr, Encoding.ASCII.GetBytes(fileName + "\0"));

            // open(path, flags, mode) - Syscall 5
            SetArgs(engine, 5, fileNameAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0, $"Failed to open {fileName}, got error {fd}");

            // write(fd, buf, count) - Syscall 4
            string content = "Hello Tmpfs";
            uint bufAddr = 0x20004000;
            mm.Mmap(bufAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);  // Trigger fault before MemWrite
            engine.MemWrite(bufAddr, Encoding.ASCII.GetBytes(content));
            SetArgs(engine, 4, (uint)fd, bufAddr, (uint)content.Length);
            sys.Handle(engine, 0x80);
            Assert.Equal(content.Length, (int)engine.RegRead(Reg.EAX));

            // close(fd) - Syscall 6
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // 3. Verify file exists in VFS
            var dentry = sys.PathWalk(fileName);
            Assert.NotNull(dentry);
            Assert.Equal("tmpfs", dentry.SuperBlock.Type.Name);

            // 4. Umount
            // umount(target) - Syscall 22
            SetArgs(engine, 22, mountPointAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // 5. Verify file is gone (since tmpfs is gone)
            dentry = sys.PathWalk(fileName);
            Assert.Null(dentry);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt")) System.IO.Directory.Delete("mnt", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestTmpfsMmap()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Create a file in tmpfs (mount it first)
            uint mntAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mntAddr, 4096 * 2, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            
            System.IO.Directory.CreateDirectory("tmp_mmap");
            mm.HandleFault(mntAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.MemWrite(mntAddr, Encoding.ASCII.GetBytes("/tmp_mmap\0"));
            engine.MemWrite(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            
            SetArgs(engine, 21, 0, mntAddr, fsTypeAddr, 0, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            string fileName = "/tmp_mmap/map.bin";
            uint nameAddr = 0x20002000;
            mm.Mmap(nameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(nameAddr, true, engine);
            engine.MemWrite(nameAddr, Encoding.ASCII.GetBytes(fileName + "\0"));

            // 1. Create and write some initial data
            SetArgs(engine, 5, nameAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0, $"Failed to open {fileName}, got error {fd}");
            
            byte[] initialData = new byte[4096];
            for (int i = 0; i < initialData.Length; i++) initialData[i] = 0x41; // 'A'
            uint bufAddr = 0x20003000;
            mm.Mmap(bufAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            engine.MemWrite(bufAddr, initialData);
            
            SetArgs(engine, 4, (uint)fd, bufAddr, 4096);
            sys.Handle(engine, 0x80);
            Assert.Equal(4096, (int)engine.RegRead(Reg.EAX));
            
            // 2. Mmap the file
            // mmap2(addr, len, prot, flags, fd, pgoffset) - Syscall 192
            uint mapAddr = 0x30000000;
            // Use Private to be safe
            SetArgs(engine, 192, mapAddr, 4096, (uint)(Protection.Read | Protection.Write), (uint)MapFlags.Private, (uint)fd, 0);
            sys.Handle(engine, 0x80);
            Assert.Equal((int)mapAddr, (int)engine.RegRead(Reg.EAX));

            // Trigger fault to load data
            mm.HandleFault(mapAddr, false, engine);
            byte[] mappedData = engine.MemRead(mapAddr, 10);
            Assert.Equal(0x41, mappedData[0]);

            // 3. Write to mapped memory
            byte[] newData = new byte[] { 0x42, 0x42, 0x42, 0x42 }; // 'B'
            engine.MemWrite(mapAddr + 100, newData);
            
            // 4. Verify via read() syscall
            SetArgs(engine, 19, (uint)fd, 100, 0); // lseek(fd, 100, SEEK_SET)
            sys.Handle(engine, 0x80);
            
            uint readBufAddr = 0x20004000;
            mm.Mmap(readBufAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(readBufAddr, true, engine);
            SetArgs(engine, 3, (uint)fd, readBufAddr, 4); // read(fd, buf, 4)
            sys.Handle(engine, 0x80);
            
            byte[] readBack = engine.MemRead(readBufAddr, 4);
            // Note: If private, the write to memory won't reflect in file read()
            // If shared, it would. Let's use Private and check that we can still read the original 'A'
            Assert.Equal(0x41, readBack[0]);

            // 5. Munmap
            SetArgs(engine, 91, mapAddr, 4096);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            if (System.IO.Directory.Exists("tmp_mmap")) System.IO.Directory.Delete("tmp_mmap", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }
}
