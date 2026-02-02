using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using Bifrost.Core;
using Bifrost.Native;
using Bifrost.Memory;
using Task = Bifrost.Core.Task;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    private void RegisterHandlers()
    {
        Register(1, SysExit);
        Register(3, SysRead);
        Register(4, SysWrite);
        Register(5, SysOpen);
        Register(6, SysClose);
        Register(10, SysUnlink);
        Register(20, SysGetPid);
        Register(33, SysAccess);
        Register(45, SysBrk);
        Register(54, SysIoctl);
        Register(91, SysMunmap);
        Register(119, SysSigReturn);
        Register(120, SysClone);
        Register(122, SysUname);
        Register(125, SysMprotect);
        Register(146, SysWriteV);
        Register(174, SysRtSigAction);
        Register(175, SysRtSigProcMask);
        Register(183, SysGetCwd);
        Register(192, SysMmap2);
        Register(195, SysStat64);
        Register(196, SysLstat64);
        Register(197, SysFstat64);
        Register(220, SysGetdents64);
        Register(240, SysFutex);
        Register(243, SysSetThreadArea);
        Register(252, SysExitGroup);
        Register(258, SysSetTidAddress);
    }

    private static int SysExit(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, false);
        sm.Engine.Stop();
        return 0;
    }

    private static int SysExitGroup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, true);
        sm.Engine.Stop();
        return 0;
    }

    private static int SysRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;

        try {
            var f = sm.GetFD(fd);
            if (f != null) {
                byte[] buf = new byte[count];
                int n = f.Read(buf.AsSpan(0, (int)count));
                if (n > 0) sm.Engine.MemWrite(bufAddr, buf.AsSpan(0, n));
                return n;
            }
        } catch { return -1; }

        return -9; // EBADF
    }

    private static int SysWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;

        var data = sm.Engine.MemRead(bufAddr, count);

        var f = sm.GetFD(fd);
        if (f != null) {
            try {
                int n = f.Write(data);
                f.Flush();
                return n;
            } catch { return -1; }
        }

        return -9; // EBADF
    }

    private static int SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        uint flags = a2;
        uint mode = a3;
        string hostPath = sm.ResolvePath(path);

        FileMode fileMode = FileMode.Open;
        FileAccess fileAccess = FileAccess.Read;

        uint access = flags & 3;
        if (access == 0) fileAccess = FileAccess.Read;
        else if (access == 1) fileAccess = FileAccess.Write;
        else if (access == 2) fileAccess = FileAccess.ReadWrite;

        if ((flags & 0x40) != 0) // O_CREAT
        {
            if ((flags & 0x80) != 0) fileMode = FileMode.CreateNew; // O_EXCL
            else if ((flags & 0x200) != 0) fileMode = FileMode.Create; // O_TRUNC
            else fileMode = FileMode.OpenOrCreate;
        }
        else
        {
            if ((flags & 0x200) != 0) fileMode = FileMode.Truncate;
            else fileMode = FileMode.Open;
        }
        
        try {
            if (Directory.Exists(hostPath))
            {
                return sm.AllocFD(new LinuxDirectory(hostPath));
            }
            var f = new FileStream(hostPath, fileMode, fileAccess, FileShare.ReadWrite);
            return sm.AllocFD(new LinuxFileStream(f));
        } catch {
            return -2; // ENOENT
        }
    }

    private static int SysClose(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.FreeFD((int)a1);
        return 0;
    }

    private static int SysBrk(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint newBrk = a1;
        if (newBrk == 0) return (int)sm.BrkAddr;

        if (newBrk > sm.BrkAddr) {
            uint start = (sm.BrkAddr + 0xFFF) & ~0xFFFu;
            uint end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
            {
                // Map anonymous
                sm.Mem.Mmap(start, end - start, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "HEAP", sm.Engine);
            }
            sm.BrkAddr = newBrk;
        }
        return (int)sm.BrkAddr;
    }
    
    private static int SysClone(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -38;
        
        if (sm.CloneHandler == null) return -38;
        
        var (tid, err) = sm.CloneHandler((int)a1, a2, a3, a4, a5);
        if (err != null) return -11; // EAGAIN
        
        return tid;
    }
    
    private static int SysFutex(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -38;
        
        uint uaddr = a1;
        int op = (int)a2;
        uint val = a3;
        
        int opCode = op & 0x7F;
        
        if (opCode == 0) // WAIT
        {
            var buf = sm.Engine.MemRead(uaddr, 4);
            uint currentVal = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (currentVal != val) return -11; // EWOULDBLOCK
            
            var waiter = sm.Futex.PrepareWait(uaddr);
            
            // Non-blocking: set the Task to await and yield the engine
            sm.BlockingTask = waiter.Tcs.Task;
            sm.Engine.Yield();
            
            return 0;
        }
        else if (opCode == 1) // WAKE
        {
            int count = (int)val;
            return sm.Futex.Wake(uaddr, count);
        }
        
        return -38; // ENOSYS
    }
    
    private static int SysSetThreadArea(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        
        uint uInfoAddr = a1;
        var buf = sm.Engine.MemRead(uInfoAddr, 16); 
        
        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
        
        sm.Engine.SetSegBase(Seg.GS, baseAddr);
        
        if (entry == 0xFFFFFFFF) 
        {
             BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 12);
             sm.Engine.MemWrite(uInfoAddr, buf.AsSpan(0, 4));
        }
        
        return 0;
    }
    
    private static int SysSetTidAddress(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        if (sm.GetTID != null) return sm.GetTID(sm.Engine);
        return 1;
    }
    
    private static int SysUname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.Engine.MemWrite(a1, System.Text.Encoding.ASCII.GetBytes("Linux\0"));
        return 0;
    }
    
    private static int SysGetCwd(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint bufAddr = a1;
        uint size = a2;
        string cwd = "/";
        if (cwd.Length + 1 > size) return -34; // ERANGE
        
        sm.Engine.MemWrite(bufAddr, System.Text.Encoding.ASCII.GetBytes(cwd + "\0"));
        return cwd.Length + 1;
    }
    
    private static int SysWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;
        
        var f = sm.GetFD(fd);
        if (f == null) return -9;

        int total = 0;
        for(int i=0; i<iovCnt; i++)
        {
            var baseBytes = sm.Engine.MemRead(iovAddr + (uint)i*8, 4);
            var lenBytes = sm.Engine.MemRead(iovAddr + (uint)i*8 + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(baseBytes);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            
            if (len > 0)
            {
                var data = sm.Engine.MemRead(baseAddr, len);
                f.Write(data);
                total += (int)len;
            }
        }
        f.Flush();
        return total;
    }
    
    private static int SysMmap2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        
        uint addr = a1;
        uint len = a2;
        int prot = (int)a3;
        int flags = (int)a4;
        int fd = (int)a5;
        long offset = (long)a6 * 4096;
        
        prot |= (int)(Protection.Read | Protection.Write);
        
        FileStream? f = null;
        bool isAnon = (flags & 0x20) != 0;
        
        if (!isAnon && fd != -1)
        {
            var lf = sm.GetFD(fd);
            if (lf is LinuxFileStream lfs) f = lfs.Stream;
            else if (lf != null) return -22; // EINVAL
            if (f == null && lf == null) return -9;
        }
        
        try
        {
            uint res = sm.Mem.Mmap(addr, len, (Protection)prot, (MapFlags)flags, f, offset, len, "MMAP2", sm.Engine);
            return (int)res;
        }
        catch
        {
            return -12; // ENOMEM
        }
    }
    
    private static int SysMunmap(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.Mem.Munmap(a1, a2, sm.Engine);
        return 0;
    }

    private static int SysMprotect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysGetPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm?.GetTGID != null) return sm.GetTGID(sm.Engine);
        return 1000;
    }
    
    private static int SysUnlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        string hostPath = sm.ResolvePath(path);
        try {
            File.Delete(hostPath);
            return 0;
        } catch { return -2; }
    }
    
    private static int SysAccess(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        string hostPath = sm.ResolvePath(path);
        if (File.Exists(hostPath) || Directory.Exists(hostPath)) return 0;
        return -2;
    }
    
    private static int SysIoctl(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysGetdents64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -9;

        // Path of the open directory
        string path = f.Name;
        if (!Directory.Exists(path)) return -22; // EINVAL

        try {
            var entries = Directory.GetFileSystemEntries(path);
            // We need a way to track the current offset in the directory
            // For now, let's just return all entries if it's the first call
            // Linux getdents uses file position to track progress.
            // FileStream.Position might not be usable for directory offsets.
            // Simplification: Return entries relative to Position / RecLen?
            // Actually, static static approach for now: return all at once then 0.
            
            if (f.Position > 0) return 0; // Already read all

            int writeOffset = 0;
            // Add "." and ".." manually if not in entries?
            var allEntries = new List<string> { ".", ".." };
            foreach (var e in entries) allEntries.Add(Path.GetFileName(e));

            for (int i = 0; i < allEntries.Count; i++)
            {
                string name = allEntries[i];
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                int nameLen = nameBytes.Length + 1;
                int recLen = (8 + 8 + 2 + 1 + nameLen + 7) & ~7;

                if (writeOffset + recLen > count) break;

                uint baseAddr = bufAddr + (uint)writeOffset;
                
                byte[] entry = new byte[recLen];
                BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0), (ulong)(i + 1)); // Inode
                BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), (long)(writeOffset + recLen)); // Next offset
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), (ushort)recLen);
                
                // d_type
                string fullPath = Path.Combine(path, name);
                byte dType = 8; // DT_REG
                if (Directory.Exists(fullPath)) dType = 4; // DT_DIR
                entry[18] = dType;

                Array.Copy(nameBytes, 0, entry, 19, nameBytes.Length);
                entry[19 + nameBytes.Length] = 0;

                sm.Engine.MemWrite(baseAddr, entry);
                writeOffset += recLen;
            }
            
            f.Position = writeOffset; // Use position to mark we've read
            return writeOffset;
        } catch { return -1; }
    }

    private static void WriteStat64(SyscallManager sm, uint addr, FileSystemInfo info)
    {
        byte[] buf = new byte[96];
        
        uint mode = 0;
        if (info.Attributes.HasFlag(FileAttributes.Directory)) mode |= 0x4000; // S_IFDIR
        else mode |= 0x8000; // S_IFREG
        mode |= 0x1ed; // 0755 octal
        
        long size = (info is FileInfo fi) ? fi.Length : 4096;
        
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1);
        
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(44), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(52), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)((size+511)/512));
        
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(88), 1);
        
        sm.Engine.MemWrite(addr, buf);
    }

    private static int SysStat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }
    
    private static int SysLstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }
    
    private static int ImplStat64(IntPtr state, uint ptrPath, uint ptrStat)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(ptrPath);
        string hostPath = sm.ResolvePath(path);
        try {
            FileSystemInfo info;
            if (Directory.Exists(hostPath)) {
                info = new DirectoryInfo(hostPath);
            } else if (File.Exists(hostPath)) {
                info = new FileInfo(hostPath);
            } else {
                return -2;
            }
            WriteStat64(sm, ptrStat, info);
            return 0;
        } catch { return -2; }
    }

    private static int SysFstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null) return -9;
        
        if (f is LinuxStandardStream)
        {
             byte[] buf = new byte[96];
             // S_IFCHR (0x2000) | 0600
             BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 0x2180);
             BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1); // nlink
             sm.Engine.MemWrite(a2, buf);
             return 0;
        }
        
        try {
            var info = new FileInfo(f.Name);
            WriteStat64(sm, a2, info);
            return 0;
        } catch { return -9; }
    }
    
    private static int SysRtSigAction(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static int SysRtSigProcMask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
}