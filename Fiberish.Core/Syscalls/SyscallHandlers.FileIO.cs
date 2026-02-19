using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    internal static async ValueTask<int> SysSendfile64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // ssize_t sendfile64(int out_fd, int in_fd, off64_t *offset, size_t count);
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var outFd = (int)a1;
        var inFd = (int)a2;
        var offsetPtr = a3;
        var count = (int)a4;

        if (!sm.FDs.TryGetValue(inFd, out var inFile) || !sm.FDs.TryGetValue(outFd, out var outFile))
            return -(int)Errno.EBADF;
        if (inFile == null || outFile == null) return -(int)Errno.EBADF;

        // Verify modes
        const int O_ACCMODE = 3;
        if (((int)inFile.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY) return -(int)Errno.EBADF;
        if (((int)outFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY) return -(int)Errno.EBADF;

        // Use a buffer
        var buffer = new byte[Math.Min(count, 32768)];
        var totalWritten = 0;

        try
        {
            long initialOffset = -1;
            if (offsetPtr != 0)
            {
                var offsetBytes = new byte[8];
                if (!sm.Engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
                initialOffset = BitConverter.ToInt64(offsetBytes);
            }

            var remaining = count;
            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, buffer.Length);
                var bytesRead = 0;

                if (offsetPtr != 0)
                    // Read from specific offset directly from inode
                    bytesRead = inFile.Dentry.Inode!.Read(inFile, buffer.AsSpan(0, toRead),
                        initialOffset + totalWritten);
                else
                    // Read from current position via File object
                    bytesRead = inFile.Read(buffer.AsSpan(0, toRead));

                if (bytesRead <= 0)
                    if (bytesRead == 0) // EOF
                        break;

                // Write to out_fd
                var bytesWritten = outFile.Write(buffer.AsSpan(0, bytesRead));

                if (bytesWritten < 0)
                {
                    if (totalWritten > 0) break;
                    return bytesWritten;
                }

                totalWritten += bytesWritten;
                remaining -= bytesWritten;

                if (bytesWritten < bytesRead) break;
            }

            if (offsetPtr != 0)
                if (!sm.Engine.CopyToUser(offsetPtr, BitConverter.GetBytes(initialOffset + totalWritten)))
                    return -(int)Errno.EFAULT;

            return totalWritten;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysSendfile64 failed");
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysPipe(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fdsAddr = a1;

        try
        {
            var pipe = new PipeInode();

            // Reader
            var rDentry = new Dentry("pipe:[read]", pipe, sm.Root, sm.Root.SuperBlock);
            var rFile = new VFS.LinuxFile(rDentry, FileFlags.O_RDONLY);
            var rFd = sm.AllocFD(rFile);
            // pipe.AddReader(); // Handled by File ctor -> Inode.Open

            // Writer
            var wDentry = new Dentry("pipe:[write]", pipe, sm.Root, sm.Root.SuperBlock);
            var wFile = new VFS.LinuxFile(wDentry, FileFlags.O_WRONLY);
            var wFd = sm.AllocFD(wFile);
            // pipe.AddWriter(); // Handled by File ctor -> Inode.Open

            // Write FDs to user memory
            // Write FDs to user memory
            var fds = new[] { rFd, wFd };
            if (!sm.Engine.CopyToUser(fdsAddr, MemoryMarshal.AsBytes(fds.AsSpan())))
                return -(int)Errno.EFAULT;

            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysPipe failed");
            return -(int)Errno.ENFILE;
        }
    }

    private static async ValueTask<int> SysCreat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // creat(path, mode) is open(path, O_CREAT|O_WRONLY|O_TRUNC, mode)
        return await SysOpen(state, a1, (uint)(FileFlags.O_CREAT | FileFlags.O_WRONLY | FileFlags.O_TRUNC), a2, a4, a5,
            a6);
    }

    private static int ImplOpen(SyscallManager sm, string path, uint flags, uint mode, Dentry? startAt = null)
    {
        Logger.LogInformation($"[Open] Path='{path}' Flags={flags} Mode={mode}");
        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null)
        {
            if ((flags & (uint)FileFlags.O_CREAT) != 0)
            {
                var lastSlash = path.LastIndexOf('/');
                var parentPath = lastSlash == -1 ? "" : lastSlash == 0 ? "/" : path[..lastSlash];
                var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

                var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
                if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

                var t = sm.Engine.Owner as FiberTask;
                var uid = t?.Process.EUID ?? 0;
                var gid = t?.Process.EGID ?? 0;

                try
                {
                    dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                    var finalMode = (int)mode & ~(t?.Process.Umask ?? 0);
                    parentDentry.Inode.Create(dentry, finalMode, uid, gid);
                }
                catch
                {
                    return -(int)Errno.EACCES;
                }
            }
            else
            {
                return -(int)Errno.ENOENT;
            }
        }
        else
        {
            // File exists - check for O_EXCL
            if ((flags & (uint)FileFlags.O_CREAT) != 0 && (flags & (uint)FileFlags.O_EXCL) != 0)
                return -(int)Errno.EEXIST;
        }

        try
        {
            var f = new VFS.LinuxFile(dentry, (FileFlags)flags);
            return sm.AllocFD(f);
        }
        catch
        {
            return -1;
        }
    }

    private static async ValueTask<int> SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;

        var path = sm.Engine.ReadStringSafe(a1);
        if (path == null) return -(int)Errno.EFAULT;

        return ImplOpen(sm, path, a2, a3);
    }

    private static async ValueTask<int> SysOpenAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

        var flags = a3;
        var mode = a4;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, flags, mode, startAt);
    }

    private static async ValueTask<int> SysOpenAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

        var howPtr = a3;
        var howSize = a4;

        if (howSize < 24) return -(int)Errno.EINVAL;

        var howBuf = new byte[24];
        if (!sm.Engine.CopyFromUser(howPtr, howBuf)) return -(int)Errno.EFAULT;

        var flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(0, 8));
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(8, 8));

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, (uint)flags, (uint)mode, startAt);
    }

    private static async ValueTask<int> SysDup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldfd = (int)a1;
        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        return sm.AllocFD(f);
    }

    private static async ValueTask<int> SysDup2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldfd = (int)a1;
        var newfd = (int)a2;

        if (oldfd == newfd)
        {
            if (sm.GetFD(oldfd) == null) return -(int)Errno.EBADF;
            return newfd;
        }

        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        sm.FreeFD(newfd);
        sm.FDs[newfd] = f;
        f.Dentry.Inode?.Get();
        return newfd;
    }

    private static async ValueTask<int> SysDup3(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // For now ignore flags like O_CLOEXEC
        return await SysDup2(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysPRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = a3;
        var offset = a4 | ((long)a5 << 32);

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        try
        {
            var buf = new byte[count];
            var n = f.Dentry.Inode!.Read(f, buf.AsSpan(), offset);
            if (n > 0)
                if (!sm.Engine.CopyToUser(bufAddr, buf.AsSpan(0, n)))
                    return -(int)Errno.EFAULT;
            return n;
        }
        catch
        {
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysPWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = a3;
        var offset = a4 | ((long)a5 << 32);

        var data = new byte[count];
        if (!sm.Engine.CopyFromUser(bufAddr, data)) return -(int)Errno.EFAULT;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        try
        {
            var n = f.Dentry.Inode!.Write(f, data, offset);
            return n;
        }
        catch
        {
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var iovAddr = a2;
        var iovCnt = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var totalRead = 0;
        for (var i = 0; i < iovCnt; i++)
        {
            var iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;

            var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                var buf = new byte[len];
                var n = f.Read(buf);
                if (n > 0)
                {
                    if (!sm.Engine.CopyToUser(baseAddr, buf.AsSpan(0, n))) return -(int)Errno.EFAULT;
                    totalRead += n;
                    if (n < (int)len) break; // EOF or short read
                }
                else
                {
                    break;
                }
            }
        }

        return totalRead;
    }

    private static async ValueTask<int> SysPReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var iovAddr = a2;
        var iovCnt = (int)a3;
        var offset = a4 | ((long)a5 << 32); // Modified line

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var totalRead = 0;
        for (var i = 0; i < iovCnt; i++)
        {
            var iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                var buf = new byte[len];
                var n = f.Dentry.Inode!.Read(f, buf, offset + totalRead);
                if (n > 0)
                {
                    if (!sm.Engine.CopyToUser(baseAddr, buf.AsSpan(0, n))) return -(int)Errno.EFAULT;
                    totalRead += n;
                    if (n < (int)len) break;
                }
                else
                {
                    break;
                }
            }
        }

        return totalRead;
    }

    private static async ValueTask<int> SysPWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var iovAddr = a2;
        var iovCnt = (int)a3;
        var offset = a4 | ((long)a5 << 32); // Modified line

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var totalWritten = 0;
        for (var i = 0; i < iovCnt; i++)
        {
            var iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;

            var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                var data = new byte[len];
                if (!sm.Engine.CopyFromUser(baseAddr, data)) return -(int)Errno.EFAULT;
                var n = f.Dentry.Inode!.Write(f, data, offset + totalWritten);
                if (n > 0)
                {
                    totalWritten += n;
                    if (n < (int)len) break;
                }
                else
                {
                    break;
                }
            }
        }

        return totalWritten;
    }

    internal static async ValueTask<int> SysRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        // TODO: Access checks

        var buf = new byte[count];

        while (true)
            try
            {
                var n = f.Read(buf.AsSpan(0, count));

                if (n == -(int)Errno.EAGAIN)
                {
                    if ((f.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

                    // Blocking Read
                    if (sm.Engine.Owner is not FiberTask currentTask) return -(int)Errno.EAGAIN; // Should not happen

                    Logger.LogInformation("[SysRead] fd={Fd} blocking, waiting for data or interrupt", fd);
                    
                    var token = currentTask.CreateSyscallToken();
                    await f.WaitForRead().AsTask().WaitAsync(token);

                    Logger.LogInformation("[SysRead] fd={Fd} data ready, retrying read", fd);
                    // File is ready, retry read
                    continue;
                }

                if (n >= 0)
                {
                    if (n > 0)
                    {
                        var hexData = Convert.ToHexString(buf.AsSpan(0, n));
                        Logger.LogInformation("[SysRead] fd={Fd} returning {N} bytes: {HexData}", fd, n, hexData);
                        if (!sm.Engine.CopyToUser(bufAddr, buf.AsSpan(0, n)))
                            return -(int)Errno.EFAULT;
                    }
                    return n;
                }

                return n; // Other error
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SysRead Exception: {ex}");
                return -(int)Errno.EIO;
            }
    }

    internal static async ValueTask<int> SysWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        // Verify read access to buffer before writing
        var data = new byte[count];
        if (!sm.Engine.CopyFromUser(bufAddr, data))
            return -(int)Errno.EFAULT;

        // Log write with hex dump
        var hexString = Convert.ToHexString(data);
        Logger.LogInformation($"[Write] fd={fd} count={count} data={hexString}");

        while (true)
            try
            {
                var n = f.Write(data);

                if (n == -(int)Errno.EAGAIN)
                {
                    if ((f.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

                    // Blocking Write
                    if (sm.Engine.Owner is not FiberTask currentTask) return -(int)Errno.EAGAIN;

                    var token = currentTask.CreateSyscallToken();
                    await f.WaitForWrite().AsTask().WaitAsync(token);
                    continue;
                }

                return n;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return -(int)Errno.EIO;
            }
    }

    private static async ValueTask<int> SysLseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var fd = (int)a1;
        var offset = (long)(int)a2; // signed offset
        var whence = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)f.Dentry.Inode!.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;
        return (int)newPos;
    }

    private static async ValueTask<int> SysLlseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var fd = (int)a1;
        var offset = ((long)a2 << 32) | a3;
        var resultPtr = a4;
        var whence = (int)a5;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)f.Dentry.Inode!.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;

        if (!sm.Engine.CopyToUser(resultPtr, BitConverter.GetBytes(newPos)))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysClose(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.FreeFD((int)a1);
        return 0;
    }

    private static async ValueTask<int> SysWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var iovAddr = a2;
        var iovCnt = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        Logger.LogInformation($"[WriteV] fd={fd} iovCnt={iovCnt}");

        var total = 0;
        for (var i = 0; i < iovCnt; i++)
        {
            var iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                var data = new byte[len];
                if (!sm.Engine.CopyFromUser(baseAddr, data)) return -(int)Errno.EFAULT;
                var hexString = Convert.ToHexString(data);
                Logger.LogInformation($"[WriteV] iov[{i}] len={len} data={hexString}");
                f.Write(data);
                total += (int)len;
            }
        }

        Logger.LogInformation($"[WriteV] total={total}");
        return total;
    }
}