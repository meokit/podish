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
            
            byte[] readBack = new byte[4];
            engine.CopyFromUser(readBufAddr, readBack);
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

    [Fact]
    public void TestVFSAdvanced()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            uint mntAddr = 0x20000000;
            uint fsTypeAddr = 0x20001000;
            mm.Mmap(mntAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(mntAddr, true, engine);
            mm.HandleFault(fsTypeAddr, true, engine);
            engine.CopyToUser(mntAddr, Encoding.ASCII.GetBytes("/mnt_adv\0"));
            engine.CopyToUser(fsTypeAddr, Encoding.ASCII.GetBytes("tmpfs\0"));
            System.IO.Directory.CreateDirectory("mnt_adv");
            SetArgs(engine, 21, 0, mntAddr, fsTypeAddr, 0, 0); // mount
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // 1. Rename Test
            uint oldNameAddr = 0x20002000;
            uint newNameAddr = 0x20003000;
            mm.Mmap(oldNameAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(oldNameAddr, true, engine);
            mm.HandleFault(newNameAddr, true, engine);
            engine.CopyToUser(oldNameAddr, Encoding.ASCII.GetBytes("/mnt_adv/old.txt\0"));
            engine.CopyToUser(newNameAddr, Encoding.ASCII.GetBytes("/mnt_adv/new.txt\0"));

            // Create old.txt
            SetArgs(engine, 5, oldNameAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            Assert.True((int)engine.RegRead(Reg.EAX) >= 0);
            SetArgs(engine, 6, engine.RegRead(Reg.EAX)); // close
            sys.Handle(engine, 0x80);

            // rename(old, new) - 38
            SetArgs(engine, 38, oldNameAddr, newNameAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            Assert.Null(sys.PathWalk("/mnt_adv/old.txt"));
            Assert.NotNull(sys.PathWalk("/mnt_adv/new.txt"));

            // 2. Link/Unlink Test (Hard Links)
            uint linkNameAddr = 0x20004000;
            mm.Mmap(linkNameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(linkNameAddr, true, engine);
            engine.CopyToUser(linkNameAddr, Encoding.ASCII.GetBytes("/mnt_adv/link.txt\0"));

            // link(old, new) - 9
            SetArgs(engine, 9, newNameAddr, linkNameAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            var d1 = sys.PathWalk("/mnt_adv/new.txt");
            var d2 = sys.PathWalk("/mnt_adv/link.txt");
            Assert.NotNull(d1);
            Assert.NotNull(d2);
            Assert.Equal(d1.Inode, d2.Inode);
            Assert.Equal(2, d1.Inode!.Dentries.Count);

            // unlink(link) - 10
            SetArgs(engine, 10, linkNameAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
            Assert.Null(sys.PathWalk("/mnt_adv/link.txt"));
            Assert.NotNull(d1);
            Assert.Single(d1.Inode!.Dentries);

            // 3. Symlink/Readlink Test
            uint symNameAddr = 0x20005000;
            uint targetAddr = 0x20006000;
            mm.Mmap(symNameAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(symNameAddr, true, engine);
            mm.HandleFault(targetAddr, true, engine);
            engine.CopyToUser(symNameAddr, Encoding.ASCII.GetBytes("/mnt_adv/sym.link\0"));
            engine.CopyToUser(targetAddr, Encoding.ASCII.GetBytes("new.txt\0")); // Relative symlink

            // symlink(target, linkpath) - 83
            SetArgs(engine, 83, targetAddr, symNameAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // readlink(path, buf, bufsiz) - 85
            uint readlinkBuf = 0x20007000;
            mm.Mmap(readlinkBuf, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(readlinkBuf, true, engine);
            SetArgs(engine, 85, symNameAddr, readlinkBuf, 4096);
            sys.Handle(engine, 0x80);
            int rlLen = (int)engine.RegRead(Reg.EAX);
            Assert.Equal(7, rlLen); // "new.txt"
            byte[] rlBuf = new byte[rlLen];
            engine.CopyFromUser(readlinkBuf, rlBuf);
            Assert.Equal("new.txt", Encoding.ASCII.GetString(rlBuf));

            // 4. Getdents Test
            uint dirNameAddr = 0x20008000;
            mm.Mmap(dirNameAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(dirNameAddr, true, engine);
            engine.CopyToUser(dirNameAddr, Encoding.ASCII.GetBytes("/mnt_adv\0"));

            SetArgs(engine, 5, dirNameAddr, (uint)FileFlags.O_RDONLY, 0); // open dir
            sys.Handle(engine, 0x80);
            int dirFd = (int)engine.RegRead(Reg.EAX);
            Assert.True(dirFd >= 0);

            uint dentsBuf = 0x20009000;
            mm.Mmap(dentsBuf, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(dentsBuf, true, engine);
            
            // getdents(fd, dirp, count) - 141
            SetArgs(engine, 141, (uint)dirFd, dentsBuf, 4096);
            sys.Handle(engine, 0x80);
            int nread = (int)engine.RegRead(Reg.EAX);
            Assert.True(nread > 0);

            // Parse dirent: ino(4) + off(4) + reclen(2) + name... + pad + type(1)
            // Expecting: ".", "..", "new.txt", "sym.link"
            bool foundNew = false;
            int pos = 0;
            while (pos < nread)
            {
                byte[] recBuf = new byte[2];
                engine.CopyFromUser(dentsBuf + (uint)pos + 8, recBuf);
                ushort reclen = BinaryPrimitives.ReadUInt16LittleEndian(recBuf);
                
                byte[] nameBuf = new byte[reclen - 11];
                engine.CopyFromUser(dentsBuf + (uint)pos + 10, nameBuf);
                string name = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
                if (name == "new.txt") foundNew = true;
                pos += reclen;
            }
            Assert.True(foundNew);

            SetArgs(engine, 6, (uint)dirFd); // close
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_adv")) System.IO.Directory.Delete("mnt_adv", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== HIGH PRIORITY: Error Handling Tests ==============

    [Fact]
    public void TestELOOP_CircularSymlinks()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            // Mount tmpfs
            MountTmpfs(engine, mm, sys, "/mnt_loop");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 16384, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);
            mm.HandleFault(bufAddr + 8192, true, engine);

            // Create circular symlinks: a -> b, b -> a
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("b\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_loop/a\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256); // symlink("b", "/mnt_loop/a")
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("a\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_loop/b\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256); // symlink("a", "/mnt_loop/b")
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Try to open through the loop - should fail with ELOOP (or return null)
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("/mnt_loop/a\0"));
            SetArgs(engine, 5, bufAddr + 4096, 0, 0); // open("/mnt_loop/a", O_RDONLY)
            sys.Handle(engine, 0x80);
            int result = (int)engine.RegRead(Reg.EAX);
            Assert.True(result < 0, $"Expected error, got fd={result}"); // ELOOP or ENOENT
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_loop")) System.IO.Directory.Delete("mnt_loop", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestENOTEMPTY_RmdirNonEmpty()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_rmdir");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create subdir
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_rmdir/subdir\0"));
            SetArgs(engine, 39, bufAddr, 0755); // mkdir("/mnt_rmdir/subdir", 0755)
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Create file in subdir
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("/mnt_rmdir/subdir/file.txt\0"));
            SetArgs(engine, 5, bufAddr + 4096, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);
            SetArgs(engine, 6, (uint)fd); // close
            sys.Handle(engine, 0x80);

            // Try rmdir on non-empty directory - should fail
            SetArgs(engine, 40, bufAddr); // rmdir("/mnt_rmdir/subdir")
            sys.Handle(engine, 0x80);
            int result = (int)engine.RegRead(Reg.EAX);
            Assert.True(result < 0, $"Expected ENOTEMPTY, got {result}");
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_rmdir")) System.IO.Directory.Delete("mnt_rmdir", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestEEXIST_CreateExisting()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_exist");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);

            // Create file
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_exist/file.txt\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);
            SetArgs(engine, 6, (uint)fd); // close
            sys.Handle(engine, 0x80);

            // Try create with O_CREAT | O_EXCL - should fail with EEXIST
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int result = (int)engine.RegRead(Reg.EAX);
            Assert.Equal(-(int)Errno.EEXIST, result);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_exist")) System.IO.Directory.Delete("mnt_exist", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestEBUSY_UmountWithOpenFile()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_busy");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create and keep open a file
            engine.MemWrite(bufAddr, Encoding.ASCII.GetBytes("/mnt_busy/open.txt\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            // Try umount - should fail with EBUSY
            engine.MemWrite(bufAddr + 4096, Encoding.ASCII.GetBytes("/mnt_busy\0"));
            SetArgs(engine, 22, bufAddr + 4096); // umount
            sys.Handle(engine, 0x80);
            int result = (int)engine.RegRead(Reg.EAX);
            Assert.Equal(-16, result); // EBUSY

            // Close the file
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Now umount should succeed
            SetArgs(engine, 22, bufAddr + 4096);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_busy")) System.IO.Directory.Delete("mnt_busy", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== HIGH PRIORITY: Reference Counting Tests ==============

    [Fact]
    public void TestUnlinkOpenFile()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_unlink");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create file with content
            engine.MemWrite(bufAddr, Encoding.ASCII.GetBytes("/mnt_unlink/file.txt\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            byte[] data = Encoding.ASCII.GetBytes("Hello, World!");
            engine.MemWrite(bufAddr + 4096, data);
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, (uint)data.Length); // write
            sys.Handle(engine, 0x80);
            Assert.Equal(data.Length, (int)engine.RegRead(Reg.EAX));

            // Unlink while file is open
            SetArgs(engine, 10, bufAddr); // unlink
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Verify file is gone from directory
            Assert.Null(sys.PathWalk("/mnt_unlink/file.txt"));

            // But we can still read from the open fd!
            SetArgs(engine, 19, (uint)fd, 0, 0); // lseek to start
            sys.Handle(engine, 0x80);

            byte[] readBuf = new byte[data.Length];
            SetArgs(engine, 3, (uint)fd, bufAddr + 4096, (uint)data.Length); // read
            sys.Handle(engine, 0x80);
            Assert.Equal(data.Length, (int)engine.RegRead(Reg.EAX));

            byte[] actualData = new byte[data.Length];
            engine.CopyFromUser(bufAddr + 4096, actualData);
            Assert.Equal("Hello, World!", Encoding.ASCII.GetString(actualData));

            // Close
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_unlink")) System.IO.Directory.Delete("mnt_unlink", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestHardLinkLifecycle()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_link");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create original file
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_link/original\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            byte[] data = Encoding.ASCII.GetBytes("shared content");
            engine.CopyToUser(bufAddr + 4096, data);
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, (uint)data.Length);
            sys.Handle(engine, 0x80);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Create hard link
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_link/hardlink\0"));
            SetArgs(engine, 9, bufAddr, bufAddr + 256); // link(original, hardlink)
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Verify same inode
            var d1 = sys.PathWalk("/mnt_link/original");
            var d2 = sys.PathWalk("/mnt_link/hardlink");
            Assert.NotNull(d1);
            Assert.NotNull(d2);
            Assert.Equal(d1.Inode!.Ino, d2.Inode!.Ino);
            Assert.Equal(2, d1.Inode!.Dentries.Count);

            // Unlink original - hardlink should still work
            SetArgs(engine, 10, bufAddr);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            Assert.Null(sys.PathWalk("/mnt_link/original"));
            d2 = sys.PathWalk("/mnt_link/hardlink");
            Assert.NotNull(d2);
            Assert.Single(d2.Inode!.Dentries);

            // Read through hardlink still works
            SetArgs(engine, 5, bufAddr + 256, (uint)FileFlags.O_RDONLY, 0);
            sys.Handle(engine, 0x80);
            fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            SetArgs(engine, 3, (uint)fd, bufAddr + 4096, (uint)data.Length);
            sys.Handle(engine, 0x80);
            byte[] linkData = new byte[data.Length];
            engine.CopyFromUser(bufAddr + 4096, linkData);
            Assert.Equal("shared content", Encoding.ASCII.GetString(linkData));

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_link")) System.IO.Directory.Delete("mnt_link", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== MEDIUM PRIORITY: Symlink Complex Scenarios ==============

    [Fact]
    public void TestSymlinkAbsoluteVsRelative()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_sym");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 16384, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            for (uint i = 0; i < 4; i++) mm.HandleFault(bufAddr + i * 4096, true, engine);

            // Create directory structure
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_sym/dir\0"));
            SetArgs(engine, 39, bufAddr, 0755);
            sys.Handle(engine, 0x80);

            // Create target file
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_sym/dir/target.txt\0"));
            SetArgs(engine, 5, bufAddr + 256, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("target content"));
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, 14);
            sys.Handle(engine, 0x80);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Absolute symlink
            engine.CopyToUser(bufAddr + 512, Encoding.ASCII.GetBytes("/mnt_sym/dir/target.txt\0")); // target
            engine.CopyToUser(bufAddr + 768, Encoding.ASCII.GetBytes("/mnt_sym/abs_link\0")); // path
            SetArgs(engine, 83, bufAddr + 512, bufAddr + 768);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Relative symlink
            engine.CopyToUser(bufAddr + 512, Encoding.ASCII.GetBytes("dir/target.txt\0"));
            engine.CopyToUser(bufAddr + 768, Encoding.ASCII.GetBytes("/mnt_sym/rel_link\0"));
            SetArgs(engine, 83, bufAddr + 512, bufAddr + 768);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Both should resolve to same content
            foreach (var link in new[] { "/mnt_sym/abs_link", "/mnt_sym/rel_link" })
            {
                engine.CopyToUser(bufAddr + 1024, Encoding.ASCII.GetBytes(link + "\0"));
                SetArgs(engine, 5, bufAddr + 1024, (uint)FileFlags.O_RDONLY, 0);
                sys.Handle(engine, 0x80);
                fd = (int)engine.RegRead(Reg.EAX);
                Assert.True(fd >= 0, $"Failed to open {link}");

                SetArgs(engine, 3, (uint)fd, bufAddr + 8192, 14);
                sys.Handle(engine, 0x80);
                byte[] cBuf = new byte[14];
                engine.CopyFromUser(bufAddr + 8192, cBuf);
                var content = Encoding.ASCII.GetString(cBuf);
                Assert.Equal("target content", content);

                SetArgs(engine, 6, (uint)fd);
                sys.Handle(engine, 0x80);
            }
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_sym")) System.IO.Directory.Delete("mnt_sym", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestSymlinkChain()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_chain");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create real file
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_chain/real.txt\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("chain test"));
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, 10);
            sys.Handle(engine, 0x80);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Create chain: link_a -> link_b -> link_c -> real.txt
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("real.txt\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_chain/link_c\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);

            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("link_c\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_chain/link_b\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);

            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("link_b\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_chain/link_a\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);

            // Open through the chain
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_chain/link_a\0"));
            SetArgs(engine, 5, bufAddr, (uint)FileFlags.O_RDONLY, 0);
            sys.Handle(engine, 0x80);
            fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0, "Failed to open through symlink chain");

            SetArgs(engine, 3, (uint)fd, bufAddr + 4096, 10);
            sys.Handle(engine, 0x80);
            byte[] chBuf = new byte[10];
            engine.CopyFromUser(bufAddr + 4096, chBuf);
            var content = Encoding.ASCII.GetString(chBuf);
            Assert.Equal("chain test", content);

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_chain")) System.IO.Directory.Delete("mnt_chain", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestDanglingSymlink()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_dangle");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 4096, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);

            // Create symlink to nonexistent target
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("nonexistent.txt\0"));
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_dangle/broken\0"));
            SetArgs(engine, 83, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // Symlink itself exists
            var dentry = sys.PathWalk("/mnt_dangle/broken", followLink: false);
            Assert.NotNull(dentry);
            Assert.Equal(InodeType.Symlink, dentry.Inode!.Type);

            // But opening it should fail with ENOENT
            SetArgs(engine, 5, bufAddr + 256, (uint)FileFlags.O_RDONLY, 0);
            sys.Handle(engine, 0x80);
            int result = (int)engine.RegRead(Reg.EAX);
            Assert.Equal(-(int)Errno.ENOENT, result);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_dangle")) System.IO.Directory.Delete("mnt_dangle", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== MEDIUM PRIORITY: Rename Atomicity ==============

    [Fact]
    public void TestRenameOverwriteFile()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_rename");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create source file with new content
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_rename/src\0"));
            SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("new content"));
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, 11);
            sys.Handle(engine, 0x80);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Create destination file with old content
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_rename/dst\0"));
            SetArgs(engine, 5, bufAddr + 256, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            fd = (int)engine.RegRead(Reg.EAX);
            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("old content"));
            SetArgs(engine, 4, (uint)fd, bufAddr + 4096, 11);
            sys.Handle(engine, 0x80);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Rename src to dst (should overwrite)
            SetArgs(engine, 38, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // src should be gone
            Assert.Null(sys.PathWalk("/mnt_rename/src"));

            // dst should have new content
            SetArgs(engine, 5, bufAddr + 256, (uint)FileFlags.O_RDONLY, 0);
            sys.Handle(engine, 0x80);
            fd = (int)engine.RegRead(Reg.EAX);
            Assert.True(fd >= 0);

            SetArgs(engine, 3, (uint)fd, bufAddr + 4096, 11);
            sys.Handle(engine, 0x80);
            byte[] rBuf = new byte[11];
            engine.CopyFromUser(bufAddr + 4096, rBuf);
            var content = Encoding.ASCII.GetString(rBuf);
            Assert.Equal("new content", content);

            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_rename")) System.IO.Directory.Delete("mnt_rename", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestRenameDirToEmptyDir()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_rendir");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            mm.HandleFault(bufAddr, true, engine);
            mm.HandleFault(bufAddr + 4096, true, engine);

            // Create source dir with file inside
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_rendir/src_dir\0"));
            SetArgs(engine, 39, bufAddr, 0755);
            sys.Handle(engine, 0x80);

            engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("/mnt_rendir/src_dir/file.txt\0"));
            SetArgs(engine, 5, bufAddr + 4096, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
            sys.Handle(engine, 0x80);
            int fd = (int)engine.RegRead(Reg.EAX);
            SetArgs(engine, 6, (uint)fd);
            sys.Handle(engine, 0x80);

            // Create empty destination dir
            engine.CopyToUser(bufAddr + 256, Encoding.ASCII.GetBytes("/mnt_rendir/dst_dir\0"));
            SetArgs(engine, 39, bufAddr + 256, 0755);
            sys.Handle(engine, 0x80);

            // Rename src_dir to dst_dir (should replace empty dir)
            SetArgs(engine, 38, bufAddr, bufAddr + 256);
            sys.Handle(engine, 0x80);
            Assert.Equal(0, (int)engine.RegRead(Reg.EAX));

            // src_dir should be gone
            Assert.Null(sys.PathWalk("/mnt_rendir/src_dir"));

            // dst_dir should have the file
            Assert.NotNull(sys.PathWalk("/mnt_rendir/dst_dir"));
            Assert.NotNull(sys.PathWalk("/mnt_rendir/dst_dir/file.txt"));
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_rendir")) System.IO.Directory.Delete("mnt_rendir", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== MEDIUM PRIORITY: Getdents Pagination ==============

    [Fact]
    public void TestGetdentsPagination()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            MountTmpfs(engine, mm, sys, "/mnt_dents");

            uint bufAddr = 0x20000000;
            mm.Mmap(bufAddr, 16384, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "test", engine);
            for (uint i = 0; i < 4; i++) mm.HandleFault(bufAddr + i * 4096, true, engine);

            // Create many files
            const int fileCount = 30;
            for (int i = 0; i < fileCount; i++)
            {
                string name = $"/mnt_dents/file_{i:D3}.txt";
                engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes(name + "\0"));
                SetArgs(engine, 5, bufAddr, (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0644);
                sys.Handle(engine, 0x80);
                int fd = (int)engine.RegRead(Reg.EAX);
                Assert.True(fd >= 0, $"Failed to create {name}");
                SetArgs(engine, 6, (uint)fd);
                sys.Handle(engine, 0x80);
            }

            // Open directory
            engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes("/mnt_dents\0"));
            SetArgs(engine, 5, bufAddr, (uint)FileFlags.O_RDONLY, 0);
            sys.Handle(engine, 0x80);
            int dirFd = (int)engine.RegRead(Reg.EAX);
            Assert.True(dirFd >= 0);

            // Read directory entries with small buffer to force pagination
            var foundFiles = new HashSet<string>();
            int totalReads = 0;
            const int smallBufSize = 256; // Small buffer to force multiple reads

            while (totalReads < 20) // Safety limit
            {
                SetArgs(engine, 141, (uint)dirFd, bufAddr + 4096, smallBufSize); // getdents
                sys.Handle(engine, 0x80);
                int nread = (int)engine.RegRead(Reg.EAX);

                if (nread <= 0) break;

                // Parse dirent entries
                int pos = 0;
                while (pos < nread)
                {
                    ushort reclen = BinaryPrimitives.ReadUInt16LittleEndian(
                        engine.MemRead(bufAddr + 4096 + (uint)pos + 8, 2));
                    string name = Encoding.ASCII.GetString(
                        engine.MemRead(bufAddr + 4096 + (uint)pos + 10, (uint)(reclen - 11))).TrimEnd('\0');
                    
                    if (name != "." && name != "..")
                        foundFiles.Add(name);
                    
                    pos += reclen;
                }

                totalReads++;
            }

            // Should find all files
            Assert.Equal(fileCount, foundFiles.Count);
            for (int i = 0; i < fileCount; i++)
            {
                Assert.Contains($"file_{i:D3}.txt", foundFiles);
            }

            SetArgs(engine, 6, (uint)dirFd);
            sys.Handle(engine, 0x80);
        }
        finally
        {
            if (System.IO.Directory.Exists("mnt_dents")) System.IO.Directory.Delete("mnt_dents", true);
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    [Fact]
    public void TestInode_LinkReturnsDentry()
    {
        var (engine, mm, sys, task) = SetupEmu();
        try
        {
            var sb = new TmpfsSuperBlock(new FileSystemType { Name = "tmpfs" });
            var inode = sb.AllocInode();
            inode.Type = InodeType.Directory;
            var parentDentry = new Dentry("test_dir", inode, sys.Root, sb);
            
            var fileInode = sb.AllocInode();
            fileInode.Type = InodeType.File;
            var fileDentry = new Dentry("file.txt", fileInode, parentDentry, sb);
            
            var linkDentry = new Dentry("link.txt", null, parentDentry, sb);
            var result = parentDentry.Inode!.Link(linkDentry, fileInode);
            
            Assert.NotNull(result);
            Assert.Equal("link.txt", result.Name);
            Assert.Equal(fileInode, result.Inode);
            Assert.Equal(parentDentry, result.Parent);
        }
        finally
        {
            Scheduler.Remove(task);
            engine.Dispose();
        }
    }

    // ============== Helper Methods ==============

    private void MountTmpfs(Engine engine, VMAManager mm, SyscallManager sys, string mountPoint)
    {
        System.IO.Directory.CreateDirectory(mountPoint.TrimStart('/'));

        uint bufAddr = 0x30000000;
        mm.Mmap(bufAddr, 8192, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "mount", engine);
        mm.HandleFault(bufAddr, true, engine);
        mm.HandleFault(bufAddr + 4096, true, engine);

        engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes(mountPoint + "\0"));
        engine.CopyToUser(bufAddr + 4096, Encoding.ASCII.GetBytes("tmpfs\0"));
        SetArgs(engine, 21, 0, bufAddr, bufAddr + 4096, 0, 0);
        sys.Handle(engine, 0x80);
        Assert.Equal(0, (int)engine.RegRead(Reg.EAX));
    }
}
