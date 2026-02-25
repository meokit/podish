using System.Text;
using Fiberish.Core;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // Callbacks for Task interaction
    public Func<int, uint, uint, uint, uint, (int, Exception?)>? CloneHandler { get; set; }
    public Action<Engine, int, bool>? ExitHandler { get; set; }
    public Func<Engine, int>? GetTID { get; set; }
    public Func<Engine, int>? GetTGID { get; set; }

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

    public Dentry? PathWalk(string path, Dentry? startAt = null, bool followLink = true, int recursion = 0)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (recursion > 40) return null; // ELOOP

        // Console.WriteLine($"PathWalk: {path}");
        Dentry current;
        if (path.StartsWith("/"))
            current = ProcessRoot;
        else
            current = startAt ?? CurrentWorkingDirectory;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (current == ProcessRoot) continue;

                if (current == current.SuperBlock.Root)
                    if (current.Mount != null) // Changed from current.MountedAt
                        current = current.Mount.MountPoint; // Changed from current.MountedAt

                if (current.Parent != null) current = current.Parent;
                continue;
            }

            // Down
            // If current is a mount point, traverse into it
            if (current.IsMounted && current.Mount != null) current = current.Mount.Root; // Changed from current.MountRoot

            Dentry? nextDentry;
            if (current.Children.TryGetValue(part, out var cached))
            {
                nextDentry = cached;
            }
            else
            {
                nextDentry = current.Inode!.Lookup(part);
                if (nextDentry == null) return null;

                current.Children[part] = nextDentry;
            }

            // Handle Symlink
            if (nextDentry.Inode!.Type == InodeType.Symlink && (followLink || i < parts.Length - 1))
            {
                var target = nextDentry.Inode.Readlink();
                var resolved = PathWalk(target, current, followLink, recursion + 1);
                if (resolved == null) return null;
                current = resolved;
            }
            else
            {
                current = nextDentry;
            }
        }

        if (current.IsMounted && current.Mount != null) current = current.Mount.Root; // Changed from current.MountRoot

        return current;
    }

    public int AllocFD(VFS.LinuxFile linuxFile, int minFd = 3)
    {
        var fd = minFd;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = linuxFile;
        return fd;
    }

    public int DupFD(VFS.LinuxFile linuxFile, int minFd = 3)
    {
        linuxFile.Get();
        return AllocFD(linuxFile, minFd);
    }

    public VFS.LinuxFile? GetFD(int fd)
    {
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public void FreeFD(int fd)
    {
        if (FDs.Remove(fd, out var f)) f.Close();
    }

    public (Dentry? dentry, string guestPath) ResolvePath(string path, bool isHostRelativeDefault = false)
    {
        string guestPath;
        Dentry? dentry;

        if (path.StartsWith("/") || !isHostRelativeDefault)
        {
            // Absolute guest path OR relative guest path (not defaulting to host)
            dentry = PathWalk(path);
            if (dentry != null || !isHostRelativeDefault)
            {
                guestPath = path;
                return (dentry, guestPath);
            }
        }

        // Host resolution (relative path or absolute host path during bootstrap)
        var hostPath = Path.GetFullPath(path);

        // Try to find if it fits in VFS
        dentry = null;
        guestPath = path; // Default guest path to the input if not internal

        // Find host root
        string? hostRoot = null;
        HostSuperBlock? hsb = null;

        if (Root.SuperBlock is HostSuperBlock h)
        {
            hsb = h;
            hostRoot = h.HostRoot;
        }
        else if (Root.SuperBlock is OverlaySuperBlock osb && osb.LowerSB is HostSuperBlock lh)
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

                dentry = PathWalk(vfsLookupPath);
                if (dentry != null) guestPath = vfsLookupPath;
            }
        }

        if (dentry == null && hsb != null && File.Exists(hostPath))
            try
            {
                dentry = hsb.GetDentry(hostPath, Path.GetFileName(hostPath), null);
            }
            catch
            {
                /* ignore */
            }

        return (dentry, guestPath);
    }
}

// Wait syscall support
