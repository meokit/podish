using System.Text;
using System.Linq;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

/// <summary>
///     Represents a location in the VFS: dentry + mount.
///     In Linux kernel, this is equivalent to struct path (dentry + vfsmount).
/// </summary>
public struct PathLocation
{
    public Dentry? Dentry { get; }
    public Mount? Mount { get; }

    public PathLocation(Dentry? dentry, Mount? mount)
    {
        Dentry = dentry;
        Mount = mount;
    }

    public static PathLocation None => new(null, null);

    public bool IsValid => Dentry != null && Mount != null;
    public bool IsNull => Dentry == null;
}

public partial class SyscallManager
{
    // Callbacks for Task interaction
    public Func<int, uint, uint, uint, uint, (int, Exception?)>? CloneHandler { get; set; }
    public Action<Engine, int, bool>? ExitHandler { get; set; }
    public Func<Engine, int>? GetTID { get; set; }
    public Func<Engine, int>? GetTGID { get; set; }

    // Lazy-initialized PathWalker instance
    private PathWalker? _pathWalker;

    /// <summary>
    ///     Gets the PathWalker instance for advanced path resolution.
    /// </summary>
    public PathWalker PathWalker => _pathWalker ??= new PathWalker(this);

    public string ReadString(uint addr)
    {
        if (addr == 0) return "";

        var sb = new StringBuilder();
        var current = addr;
        var buf = new byte[1];
        while (true)
        {
            if (!Engine.CopyFromUser(current++, buf)) break;
            if (buf[0] == 0) break;
            sb.Append((char)buf[0]);
            if (sb.Length > 4096) break; // Safety limit
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Walk a path and return both dentry and mount information.
    ///     This is the core path resolution function, similar to Linux's path_lookup().
    ///     Uses the new PathWalker implementation with NameData pattern.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="startAt">Optional starting location (default for cwd)</param>
    /// <param name="followLink">Whether to follow symlinks on final component</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalk(string path, PathLocation? startAt = null, bool followLink = true)
    {
        // Use the new PathWalker implementation
        return PathWalker.PathWalk(path, startAt, followLink);
    }

    /// <summary>
    ///     Walk a path with explicit LookupFlags.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="flags">Lookup flags controlling behavior</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalkWithFlags(string path, LookupFlags flags)
    {
        return PathWalker.PathWalk(path, flags);
    }

    /// <summary>
    ///     Walk a path with explicit starting location and flags.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="startAt">Starting path location</param>
    /// <param name="flags">Lookup flags controlling behavior</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalkWithFlags(string path, PathLocation startAt, LookupFlags flags)
    {
        return PathWalker.PathWalk(path, startAt, flags);
    }

    /// <summary>
    ///     Walk a path and return full NameData with resolution state.
    ///     Useful for create operations where you need the final component name.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="flags">Lookup flags controlling behavior</param>
    /// <returns>NameData with full resolution state</returns>
    public NameData PathWalkWithData(string path, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        return PathWalker.PathWalkWithData(path, flags);
    }

    /// <summary>
    ///     Prepare for a create operation - resolve parent directory and extract name.
    /// </summary>
    /// <param name="path">Path where file will be created</param>
    /// <param name="startAt">Optional starting location</param>
    /// <returns>Tuple of (parent location, filename, error code)</returns>
    public (PathLocation parent, string name, int error) PathWalkForCreate(string path, PathLocation? startAt = null)
    {
        return PathWalker.PathWalkForCreate(path, startAt);
    }

    public int AllocFD(LinuxFile linuxFile, int minFd = 0, bool? closeOnExec = null)
    {
        var fd = minFd;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = linuxFile;
        var cloexec = closeOnExec ?? ((linuxFile.Flags & FileFlags.O_CLOEXEC) != 0);
        if (cloexec)
            FdCloseOnExecSet.Add(fd);
        else
            FdCloseOnExecSet.Remove(fd);
        return fd;
    }

    public int DupFD(LinuxFile linuxFile, int minFd = 0, bool closeOnExec = false)
    {
        linuxFile.Get();
        return AllocFD(linuxFile, minFd, closeOnExec);
    }

    public int DupFD(int oldFd, int minFd = 0, bool closeOnExec = false)
    {
        var file = GetFD(oldFd);
        if (file == null) return -(int)Errno.EBADF;
        return DupFD(file, minFd, closeOnExec);
    }

    public LinuxFile? GetFD(int fd)
    {
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public bool IsFdCloseOnExec(int fd)
    {
        return FdCloseOnExecSet.Contains(fd);
    }

    public void SetFdCloseOnExec(int fd, bool closeOnExec)
    {
        if (!FDs.ContainsKey(fd)) return;
        if (closeOnExec)
            FdCloseOnExecSet.Add(fd);
        else
            FdCloseOnExecSet.Remove(fd);
    }

    public IReadOnlyList<int> GetCloseOnExecFds()
    {
        return FdCloseOnExecSet.ToList();
    }

    public void FreeFD(int fd)
    {
        TryFreeFD(fd);
    }

    public bool TryFreeFD(int fd)
    {
        FdCloseOnExecSet.Remove(fd);
        if (!FDs.Remove(fd, out var f)) return false;
        f.Close();
        return true;
    }

    public void CloseAllFileDescriptors()
    {
        var fds = FDs.Keys.ToList();
        foreach (var fd in fds)
            FreeFD(fd);
        FdCloseOnExecSet.Clear();
    }

    /// <summary>
    ///     Best-effort container-wide writeback.
    ///     Flushes shared mmap dirty pages, open-file sync paths, and mounted superblock page caches.
    /// </summary>
    public void SyncContainerPageCache()
    {
        var scheduler = (Engine.Owner as FiberTask)?.CommonKernel ?? KernelScheduler.Current;
        var managers = new HashSet<SyscallManager>();
        var managerToProcess = new Dictionary<SyscallManager, Process>();
        if (scheduler != null)
            foreach (var process in scheduler.GetProcessesSnapshot())
                if (process.Syscalls != null)
                {
                    managers.Add(process.Syscalls);
                    managerToProcess[process.Syscalls] = process;
                }

        if (managers.Count == 0)
        {
            managers.Add(this);
            if (Engine.Owner is FiberTask fallbackTask)
                managerToProcess[this] = fallbackTask.Process;
        }

        foreach (var manager in managers)
            try
            {
                managerToProcess.TryGetValue(manager, out var process);
                ProcessAddressSpaceSync.SyncAllMappedSharedFiles(manager.Mem, manager.Engine, process);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "SyncAllMappedSharedFiles failed for one process");
            }

        foreach (var manager in managers)
            foreach (var file in manager.FDs.Values)
                try
                {
                    file?.OpenedInode?.Sync(file);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Inode.Sync failed for one open file");
                }

        var superblocks = new HashSet<SuperBlock>();
        foreach (var manager in managers)
            foreach (var mount in manager.Mounts)
                if (mount?.SB != null)
                    superblocks.Add(mount.SB);

        foreach (var sb in superblocks)
        {
            List<Inode> inodes;
            lock (sb.Lock)
            {
                inodes = sb.Inodes.ToList();
            }

            foreach (var inode in inodes)
            {
                if (inode.PageCache == null) continue;
                try
                {
                    _ = inode.WritePages(null, new WritePagesRequest(0, long.MaxValue, true));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "WritePages failed for one inode");
                }
            }
        }
    }

    public (PathLocation loc, string guestPath) ResolvePath(string path, bool isHostRelativeDefault = false)
    {
        string guestPath;
        PathLocation loc;

        if (path.StartsWith("/") || !isHostRelativeDefault)
        {
            // Absolute guest path OR relative guest path (not defaulting to host)
            loc = PathWalk(path);
            if (loc.IsValid || !isHostRelativeDefault)
            {
                guestPath = path;
                return (loc, guestPath);
            }
        }

        // Host resolution (relative path or absolute host path during bootstrap)
        var hostPath = Path.GetFullPath(path);

        // Try to find if it fits in VFS
        loc = PathLocation.None;
        guestPath = path; // Default guest path to the input if not internal

        // Find host root
        string? hostRoot = null;
        HostSuperBlock? hsb = null;

        if (Root.Mount!.SB is HostSuperBlock h)
        {
            hsb = h;
            hostRoot = h.HostRoot;
        }
        else if (Root.Mount.SB is OverlaySuperBlock osb && osb.LowerSB is HostSuperBlock lh)
        {
            hsb = lh;
            hostRoot = lh.HostRoot;
        }

        if (hostRoot != null)
        {
            hostRoot = Path.GetFullPath(hostRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (hostPath.StartsWith(hostRoot, StringComparison.OrdinalIgnoreCase))
            {
                var vfsLookupPath = hostPath[hostRoot.Length..];
                if (string.IsNullOrEmpty(vfsLookupPath)) vfsLookupPath = "/";
                else if (vfsLookupPath[0] != Path.DirectorySeparatorChar && vfsLookupPath[0] != '/')
                    vfsLookupPath = "/" + vfsLookupPath;
                vfsLookupPath = vfsLookupPath.Replace(Path.DirectorySeparatorChar, '/');

                loc = PathWalk(vfsLookupPath);
                if (loc.IsValid) guestPath = vfsLookupPath;
            }
        }

        if (!loc.IsValid && hsb != null && File.Exists(hostPath))
            try
            {
                var dentry = hsb.GetDentry(hostPath, Path.GetFileName(hostPath), null);
                // For hostfs bootstrap, we might not have a proper mount yet, but 
                // typically this is used when Root is already set up.
                loc = new PathLocation(dentry, Root.Mount);
            }
            catch
            {
                /* ignore */
            }

        return (loc, guestPath);
    }
}

// Wait syscall support
