using Xunit;
using System;
using System.IO;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Native;
using Bifrost.VFS;

namespace Bifrost.Tests;

public class DirtySyncTests
{
    [Fact]
    public void TestSharedVmaDirtyWriteback()
    {
        string testFile = "test_shared_vma_sync.bin";
        string fullPath = Path.GetFullPath(testFile);
        if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        
        try 
        {
            // 1. Create a file with some initial content
            byte[] initialData = new byte[8192];
            for (int i = 0; i < initialData.Length; i++) initialData[i] = 0xAA;
            System.IO.File.WriteAllBytes(fullPath, initialData);
            
            using var engine = new Engine();
            var vmaManager = new VMAManager();
            
            // 2. Mmap the file as SHARED using VFS
            var hostFs = new Hostfs();
            var fsType = new FileSystemType { Name = "hostfs", FileSystem = hostFs };
            var sb = hostFs.ReadSuper(fsType, 0, Path.GetDirectoryName(fullPath)!, null);
            var dentry = sb.Root.Inode!.Lookup(Path.GetFileName(fullPath));
            Assert.NotNull(dentry);
            var vfsFile = new Bifrost.VFS.File(dentry, FileFlags.O_RDWR);

            uint addr = 0x10000000;
            vmaManager.Mmap(addr, 8192, Protection.Read | Protection.Write, MapFlags.Shared, vfsFile, 0, 8192, "shared_file", engine);
            
            // 3. Manually trigger a fault to map the first page and "load" from file
            vmaManager.HandleFault(addr, true, engine);
            vmaManager.HandleFault(addr + 4096, true, engine);
            
            // Verify initial content in memory
            byte[] memData = engine.MemRead(addr, 1);
            Assert.Equal(0xAA, memData[0]);
            
            // 4. Modify memory
            byte[] newData = new byte[] { 0xBB, 0xCC, 0xDD, 0xEE };
            engine.MemWrite(addr + 10, newData);
            engine.MemWrite(addr + 4096 + 20, newData);
            
            // Check dirty bits via Engine (triggered by MemWrite)
            Assert.True(engine.IsDirty(addr), "Page 1 should be marked dirty in x86 MMU");
            Assert.True(engine.IsDirty(addr + 4096), "Page 2 should be marked dirty in x86 MMU");
            
            // 5. Munmap - this MUST trigger SyncVMA for shared mappings
            vmaManager.Munmap(addr, 8192, engine);
            
            // 6. Verify file content
            vfsFile.Close();
            byte[] fileData = System.IO.File.ReadAllBytes(fullPath);
            
            Assert.Equal(0xBB, fileData[10]);
            Assert.Equal(0xCC, fileData[11]);
            Assert.Equal(0xBB, fileData[4096 + 20]);
            Assert.Equal(0xCC, fileData[4096 + 21]);
        }
        finally
        {
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        }
    }
}
