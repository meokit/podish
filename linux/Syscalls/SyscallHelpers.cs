using System.Text;
using Bifrost.Core;
using Bifrost.VFS;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    // Callbacks for Task interaction
    public Func<int, uint, uint, uint, uint, (int, Exception?)>? CloneHandler { get; set; }
    public Action<Engine, int, bool>? ExitHandler { get; set; }
    public Func<Engine, int>? GetTID { get; set; }
    public Func<Engine, int>? GetTGID { get; set; }

    public string ReadString(uint addr)
    {
        if (addr == 0) return "";
        
        try
        {
            var sb = new StringBuilder();
            uint current = addr;
            while (true)
            {
                var b = Engine.MemRead(current++, 1)[0];
                if (b == 0) break;
                sb.Append((char)b);
                if (sb.Length > 4096) break; // Safety limit
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    public Dentry? PathWalk(string path, bool followLink = true)
    {
        if (string.IsNullOrEmpty(path)) return null;
        
        // Console.WriteLine($"PathWalk: {path}");
        Dentry current = path.StartsWith("/") ? ProcessRoot : CurrentWorkingDirectory;
        
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (current == ProcessRoot) continue;
                
                if (current == current.SuperBlock.Root)
                {
                    if (current.MountedAt != null)
                    {
                        current = current.MountedAt;
                    }
                }
                
                if (current.Parent != null)
                {
                    current = current.Parent;
                }
                continue;
            }

            // Down
            // If current is a mount point, traverse into it
            if (current.IsMounted && current.MountRoot != null)
            {
                current = current.MountRoot;
            }

            if (current.Children.TryGetValue(part, out var cached))
            {
                current = cached;
            }
            else
            {
                var nextInode = current.Inode.Lookup(part);
                if (nextInode == null) 
                {
                    // Console.WriteLine($"PathWalk: Failed to find '{part}' in '{current.Name}'");
                    return null;
                }
                
                // Create a new Dentry in the tree
                var nextDentry = new Dentry(part, nextInode, current, current.SuperBlock);
                current.Children[part] = nextDentry;
                
                // If it's a TmpfsInode, we should link it (though Tmpfs.Lookup should have done it)
                if (nextInode is TmpfsInode ti)
                {
                    ti.SetPrimaryDentry(nextDentry);
                }
                
                current = nextDentry;
            }
        }
        
        // Final check: if the result is a mount point, we should probably return the mount root?
        if (current.IsMounted && current.MountRoot != null)
        {
            current = current.MountRoot;
        }
        
        return current;
    }

    public int AllocFD(Bifrost.VFS.File file)
    {
        int fd = 3;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = file;
        return fd;
    }

    public Bifrost.VFS.File? GetFD(int fd)
    {
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public void FreeFD(int fd)
    {
        if (FDs.Remove(fd, out var f))
        {
            f.Close();
        }
    }
}
