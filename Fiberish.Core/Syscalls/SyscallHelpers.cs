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
                    if (current.MountedAt != null)
                        current = current.MountedAt;

                if (current.Parent != null) current = current.Parent;
                continue;
            }

            // Down
            // If current is a mount point, traverse into it
            if (current.IsMounted && current.MountRoot != null) current = current.MountRoot;

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

        if (current.IsMounted && current.MountRoot != null) current = current.MountRoot;

        return current;
    }

    public int AllocFD(VFS.LinuxFile linuxFile, int minFd = 3)
    {
        var fd = minFd;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = linuxFile;
        return fd;
    }

    public VFS.LinuxFile? GetFD(int fd)
    {
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public void FreeFD(int fd)
    {
        if (FDs.Remove(fd, out var f)) f.Close();
    }
}

// Wait syscall support