using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

/// <summary>
///     Inode for a PTY slave device in devpts (/dev/pts/N).
/// </summary>
public class PtySlaveInode : Inode
{
    private readonly ILogger _logger;

    public PtySlaveInode(SuperBlock sb, PtyPair ptyPair, ILogger logger)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 0666
        PtyPair = ptyPair;
        _logger = logger;
        Ino = (ulong)(ptyPair.Index + 1); // Inode number is PTY index + 1
        Rdev = PtyManager.GetPtsRdev(ptyPair.Index);
        ATime = MTime = CTime = DateTime.UtcNow;
    }

    /// <summary>
    ///     Gets the PTY pair this slave belongs to.
    /// </summary>
    public PtyPair PtyPair { get; }

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return PtyPair.Slave.Read(buffer, linuxFile.Flags);
    }

    public override async ValueTask WaitForRead(LinuxFile linuxFile)
    {
        await PtyPair.Slave.DataAvailable;
        // Reset after waking up
        PtyPair.Slave.DataAvailable.Reset();
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return PtyPair.Slave.Write(buffer);
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;

        short revents = 0;

        if ((events & POLLIN) != 0 && PtyPair.Slave.HasDataAvailable)
            revents |= POLLIN;

        // PTY slave is always writable (simplified)
        if ((events & POLLOUT) != 0)
            revents |= POLLOUT;

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        const short POLLIN = 0x0001;
        if ((events & POLLIN) != 0)
        {
            PtyPair.Slave.DataAvailable.Register(callback);
            return true;
        }

        return false;
    }

    public override int Ioctl(LinuxFile linuxFile, uint request, uint arg, Engine engine)
    {
        // Delegate to TTY discipline if available
        var discipline = PtyPair.Slave.Discipline;
        if (discipline != null)
            return discipline.Ioctl(request, arg, engine);

        return -(int)Errno.ENOTTY;
    }

    public override void Truncate(long size)
    {
    }
}

/// <summary>
///     Inode for the PTY master multiplexer (/dev/ptmx).
/// </summary>
public class PtmxInode : Inode
{
    private readonly ISignalBroadcaster _broadcaster;
    private readonly ILogger _logger;
    private readonly PtyManager _ptyManager;

    public PtmxInode(SuperBlock sb, PtyManager ptyManager, ISignalBroadcaster broadcaster, ILogger logger)
    {
        SuperBlock = sb;
        Type = InodeType.CharDev;
        Mode = 0x1B6; // 0666
        _ptyManager = ptyManager;
        _broadcaster = broadcaster;
        _logger = logger;
        Ino = 2; // Fixed inode number for ptmx
        Rdev = PtyManager.GetPtmxRdev();
        ATime = MTime = CTime = DateTime.UtcNow;
    }

    /// <summary>
    ///     Allocates a new PTY pair and returns the master file descriptor info.
    ///     This is called when opening /dev/ptmx.
    /// </summary>
    public PtyPair? AllocatePty()
    {
        return _ptyManager.AllocatePty();
    }

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        // Get the PTY pair from the file's private data
        if (linuxFile.PrivateData is not PtyPair pair)
            return -(int)Errno.EBADF;

        return pair.Master.Read(buffer, linuxFile.Flags);
    }

    public override async ValueTask WaitForRead(LinuxFile linuxFile)
    {
        if (linuxFile.PrivateData is not PtyPair pair)
            return;

        await pair.Master.DataAvailable;
        pair.Master.DataAvailable.Reset();
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (linuxFile.PrivateData is not PtyPair pair)
            return -(int)Errno.EBADF;

        return pair.Master.Write(buffer);
    }

    public override short Poll(LinuxFile linuxFile, short events)
    {
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;

        short revents = 0;

        if (linuxFile.PrivateData is PtyPair pair)
        {
            if ((events & POLLIN) != 0 && pair.Master.HasDataAvailable)
                revents |= POLLIN;

            // PTY master is always writable (simplified)
            if ((events & POLLOUT) != 0)
                revents |= POLLOUT;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        if (linuxFile.PrivateData is not PtyPair pair)
            return false;

        const short POLLIN = 0x0001;
        if ((events & POLLIN) != 0)
        {
            pair.Master.DataAvailable.Register(callback);
            return true;
        }

        return false;
    }

    public override int Ioctl(LinuxFile linuxFile, uint request, uint arg, Engine engine)
    {
        if (linuxFile.PrivateData is not PtyPair pair)
            return -(int)Errno.EBADF;

        return pair.Master.Ioctl(request, arg, engine);
    }

    public override void Open(LinuxFile linuxFile)
    {
        // Allocate a new PTY pair when opening ptmx
        var pair = AllocatePty();
        if (pair == null)
            // Allocation failed - this will be checked by the caller
            return;

        linuxFile.PrivateData = pair;
        _logger.LogInformation("[PtmxInode] Opened ptmx, allocated PTY index={Index}", pair.Index);
    }

    public override void Release(LinuxFile linuxFile)
    {
        if (linuxFile.PrivateData is PtyPair pair)
        {
            pair.CloseMaster();
            _logger.LogInformation("[PtmxInode] Closed ptmx for PTY index={Index}", pair.Index);
        }
    }

    public override void Truncate(long size)
    {
    }
}

/// <summary>
///     Superblock for the devpts filesystem.
/// </summary>
public class DevPtsSuperBlock : SuperBlock
{
    private readonly ISignalBroadcaster _broadcaster;
    private readonly ILogger _logger;
    private readonly PtyManager _ptyManager;
    private readonly Dictionary<int, Dentry> _slaveDentries = new();

    public DevPtsSuperBlock(PtyManager ptyManager, ISignalBroadcaster broadcaster, ILogger logger)
    {
        _ptyManager = ptyManager;
        _broadcaster = broadcaster;
        _logger = logger;

        // Subscribe to PTY events
        _ptyManager.OnPtyCreated += OnPtyCreated;
        _ptyManager.OnPtyDestroyed += OnPtyDestroyed;

        // Create root directory
        Type = new FileSystemType { Name = "devpts" };
        var rootInode = new DevPtsDirectoryInode(this, ptyManager, logger);
        Root = new Dentry("", rootInode, null, this);
    }

    private void OnPtyCreated(int index, PtyPair pair)
    {
        // Create a dentry for the slave device
        var slaveInode = new PtySlaveInode(this, pair, _logger);
        var dentry = new Dentry(index.ToString(), slaveInode, Root, this);
        Root.Children[index.ToString()] = dentry;
        _slaveDentries[index] = dentry;
        _logger.LogInformation("[DevPts] Created slave dentry for PTY index={Index}", index);
    }

    private void OnPtyDestroyed(int index)
    {
        if (_slaveDentries.Remove(index, out var dentry))
        {
            Root.Children.Remove(index.ToString());
            _logger.LogInformation("[DevPts] Removed slave dentry for PTY index={Index}", index);
        }
    }
}

/// <summary>
///     Directory inode for devpts root (/dev/pts).
/// </summary>
public class DevPtsDirectoryInode : Inode
{
    private readonly ILogger _logger;
    private readonly PtyManager _ptyManager;

    public DevPtsDirectoryInode(SuperBlock sb, PtyManager ptyManager, ILogger logger)
    {
        SuperBlock = sb;
        Type = InodeType.Directory;
        Mode = 0x1FF; // 0777
        _ptyManager = ptyManager;
        _logger = logger;
        Ino = 1; // Root inode
    }

    public override Dentry? Lookup(string name)
    {
        // Check if the name is a number (PTY index)
        if (!int.TryParse(name, out var index))
            return null;

        // Check if the PTY exists
        if (!_ptyManager.PtyExists(index))
            return null;

        // The dentry should have been created by the OnPtyCreated handler
        // This is just a fallback
        return null;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = 1, Type = InodeType.Directory } // Parent is devtmpfs root
        };

        // Add entries for all active PTYs
        // This is a simplified implementation - in reality we'd iterate over _ptyManager
        return entries;
    }
}

/// <summary>
///     Filesystem implementation for devpts.
/// </summary>
public class DevPtsFileSystem : FileSystem
{
    private readonly ISignalBroadcaster _broadcaster;
    private readonly ILogger _logger;
    private readonly PtyManager _ptyManager;

    public DevPtsFileSystem(PtyManager ptyManager, ISignalBroadcaster broadcaster, ILogger logger)
    {
        Name = "devpts";
        _ptyManager = ptyManager;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        return new DevPtsSuperBlock(_ptyManager, _broadcaster, _logger);
    }
}