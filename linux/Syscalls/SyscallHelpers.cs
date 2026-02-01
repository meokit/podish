using System.Text;
using Bifrost.Core;

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

    public string ResolvePath(string path)
    {
        string guestPath;
        if (path.StartsWith("/"))
        {
            guestPath = path;
        }
        else
        {
            guestPath = Path.Combine(Cwd, path);
        }
        return Path.Combine(RootFS, guestPath.TrimStart('/'));
    }

    public int AllocFD(LinuxFile file)
    {
        int fd = 3;
        while (FDs.ContainsKey(fd)) fd++;
        FDs[fd] = file;
        return fd;
    }

    public LinuxFile? GetFD(int fd)
    {
        if (fd == 0) return new LinuxStandardStream(Console.OpenStandardInput(), "/dev/stdin");
        if (fd == 1) return new LinuxStandardStream(Console.OpenStandardOutput(), "/dev/stdout");
        if (fd == 2) return new LinuxStandardStream(Console.OpenStandardError(), "/dev/stderr");
        return FDs.TryGetValue(fd, out var f) ? f : null;
    }

    public void FreeFD(int fd)
    {
        if (FDs.Remove(fd, out var f))
        {
            f.Dispose();
        }
    }
}
