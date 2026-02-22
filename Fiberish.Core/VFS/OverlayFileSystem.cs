using Fiberish.Native;
// needed for SyscallManager Access if required, but maybe not directly here yet

namespace Fiberish.VFS;

public class OverlayFileSystem : FileSystem
{
    public OverlayFileSystem()
    {
        Name = "overlay";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        // data should contain "lowerdir=...,upperdir=...,workdir=..."
        // but for our initial implementation we might simplify or expect pre-resolved dentries passed via data if possible,
        // OR we rely on string paths and use a helper to resolve them.
        // However, standard mount passes a string.
        // To resolve paths, we need a context. But FileSystem.ReadSuper is generic.
        // Typically in Linux mount(2) happens in kernel context.
        // Here we might need to cheat and assume 'data' contains resolved dentries or we parse strings using a global context?
        // Actually, SyscallHandlers.SysMount resolves the dentries. But standard mount(2) passes string options.
        // Let's assume for now we receive a special configuration object or string.

        // BETTER APPROACH: SyscallHandlers.SysMount parses the options and looks up the paths using the current process context,
        // then passes the resolved Dentries to ReadSuper via the `data` object.

        if (data is not OverlayMountOptions options)
            throw new ArgumentException("OverlayFS requires OverlayMountOptions in data");

        var sb = new OverlaySuperBlock(fsType, options.Lower, options.Upper);
        sb.Root = new Dentry("/", new OverlayInode(sb, options.Lower.Root, options.Upper.Root), null, sb);
        sb.Root.Parent = sb.Root;

        return sb;
    }
}

public class OverlayMountOptions
{
    public SuperBlock Lower { get; set; } = null!;
    public SuperBlock Upper { get; set; } = null!;
}

public class OverlaySuperBlock : SuperBlock
{
    public OverlaySuperBlock(FileSystemType type, SuperBlock lower, SuperBlock upper)
    {
        Type = type;
        LowerSB = lower;
        UpperSB = upper;
    }

    public SuperBlock LowerSB { get; }
    public SuperBlock UpperSB { get; }

    public override Inode AllocInode()
    {
        // Only called if we need a pure virtual inode? 
        // Overlay inodes are always bonded to underlying inodes.
        // But we might need to allocate a new OverlayInode wrapper.
        return new OverlayInode(this, null, null);
    }
}

public class OverlayInode : Inode
{
    public OverlayInode(SuperBlock sb, Dentry? lower, Dentry? upper)
    {
        SuperBlock = sb;
        LowerDentry = lower;
        UpperDentry = upper;
    }

    private Inode? SourceInode => UpperInode ?? LowerInode;

    public override ulong Ino { get => SourceInode?.Ino ?? 0; set { if (SourceInode != null) SourceInode.Ino = value; } }
    public override InodeType Type { get => SourceInode?.Type ?? InodeType.File; set { if (SourceInode != null) SourceInode.Type = value; } }
    public override int Mode { get => SourceInode?.Mode ?? 0; set { if (SourceInode != null) SourceInode.Mode = value; } }
    public override int Uid { get => SourceInode?.Uid ?? 0; set { if (SourceInode != null) SourceInode.Uid = value; } }
    public override int Gid { get => SourceInode?.Gid ?? 0; set { if (SourceInode != null) SourceInode.Gid = value; } }
    public override ulong Size 
    { 
        get {
            return SourceInode?.Size ?? 0;
        }
        set { 
            if (SourceInode != null) SourceInode.Size = value; 
        } 
    }
    public override DateTime MTime { get => SourceInode?.MTime ?? DateTime.UnixEpoch; set { if (SourceInode != null) SourceInode.MTime = value; } }
    public override DateTime ATime { get => SourceInode?.ATime ?? DateTime.UnixEpoch; set { if (SourceInode != null) SourceInode.ATime = value; } }
    public override DateTime CTime { get => SourceInode?.CTime ?? DateTime.UnixEpoch; set { if (SourceInode != null) SourceInode.CTime = value; } }

    public Dentry? LowerDentry { get; }
    public Dentry? UpperDentry { get; private set; }

    public Inode? LowerInode => LowerDentry?.Inode;
    public Inode? UpperInode => UpperDentry?.Inode;

    public void CopyUp(LinuxFile linuxFile)
    {
        if (UpperDentry != null) return;

        // Copy Up File!
        var parentOverlayDentry =
            linuxFile.Dentry.Parent ?? throw new InvalidOperationException("Cannot copy-up root?");
        var parentOverlayInode = parentOverlayDentry.Inode as OverlayInode ??
                                 throw new InvalidOperationException("Parent is not overlay inode");
        if (parentOverlayInode.UpperDentry == null)
            throw new InvalidOperationException(
                "Parent directory is lower-only. Recursive directory copy-up not implemented yet.");

        // 2. Create the file in Upper Parent
        var upperParentDentry = parentOverlayInode.UpperDentry;

        // We need a unique Dentry for the upper creation attached to the Upper Parent
        var upperDentry =
            new Dentry(linuxFile.Dentry.Name, null, upperParentDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);

        // Replicate mode/uid/gid from Lower
        parentOverlayInode.UpperInode!.Create(upperDentry, Mode, Uid, Gid);

        // 3. Copy data
        if (LowerInode != null)
        {
            // We are reading from Lower, which uses file.PrivateData (FileStream) if Hostfs.
            // We are writing to Upper (Tmpfs), which ignores file.PrivateData.

            var buf = new byte[4096];
            long pos = 0;
            while (true)
            {
                var n = LowerInode.Read(linuxFile, buf, pos);
                if (n <= 0) break;
                upperDentry.Inode!.Write(linuxFile, buf.AsSpan(0, n), pos);
                pos += n;
            }

            // IMPORTANT: Close Lower resource now that we are done with it.
            // We are switching "file" to be backed by Upper.
            LowerInode.Release(linuxFile);
        }

        UpperDentry = upperDentry;

        // Open Upper (to set up PrivateData if needed by Upper FS)
        UpperInode!.Open(linuxFile);
    }

    public override Dentry? Lookup(string name)
    {
        // 1. Lookup in Upper
        var upperDentry = UpperInode?.Lookup(name);

        // 2. Lookup in Lower
        var lowerDentry = LowerInode?.Lookup(name);

        if (upperDentry == null && lowerDentry == null) return null;

        // Create Overlay Inode
        var inode = new OverlayInode(SuperBlock, lowerDentry, upperDentry);

        return new Dentry(name, inode, null, SuperBlock);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        // Create in Upper.
        if (UpperDentry == null)
            // If this directory doesn't exist in Upper (only in Lower), we must "Copy Up" the directory structure first.
            throw new InvalidOperationException(
                "Cannot create in a read-only lower directory. (Directory Copy-Up not fully implemented yet)");

        // Delegate to Upper
        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode!.Create(upperDentry, mode, uid, gid);

        // Now update the overlay dentry's inode
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry); // Created only in upper
        dentry.Instantiate(newOverlayInode);

        return dentry;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (UpperDentry == null) throw new InvalidOperationException("Lower-only directory write not supported yet");

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode!.Mkdir(upperDentry, mode, uid, gid);

        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry);
        dentry.Instantiate(newOverlayInode);

        return dentry;
    }

    public override void Unlink(string name)
    {
        var inUpper = UpperInode?.Lookup(name) != null;
        var inLower = LowerInode?.Lookup(name) != null;

        if (inUpper) UpperInode!.Unlink(name);

        if (inLower)
        {
            // Create whiteout in Upper.
        }
    }

    public override void Rmdir(string name)
    {
        UpperInode?.Rmdir(name);
        // Lower? Whiteout for dir?
    }

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        // Read from Upper if exists, else Lower.
        if (UpperInode != null) return UpperInode.Read(linuxFile, buffer, offset);
        if (LowerInode != null) return LowerInode.Read(linuxFile, buffer, offset);
        return 0;
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        // Write to Upper.
        if (UpperInode == null) CopyUp(linuxFile);

        return UpperInode!.Write(linuxFile, buffer, offset);
    }

    public override void Open(LinuxFile linuxFile)
    {
        if (UpperInode != null) UpperInode.Open(linuxFile);
        else LowerInode?.Open(linuxFile);
    }

    public override void Release(LinuxFile linuxFile)
    {
        if (UpperInode != null) UpperInode.Release(linuxFile);
        else LowerInode?.Release(linuxFile);
    }

    public override int Truncate(long size)
    {
        if (UpperInode != null) return UpperInode.Truncate(size);
        return -(int)Errno.EROFS;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var entries = new Dictionary<string, DirectoryEntry>();

        if (LowerInode != null)
            foreach (var e in LowerInode.GetEntries())
                entries[e.Name] = e;

        if (UpperInode != null)
            foreach (var e in UpperInode.GetEntries())
                entries[e.Name] = e;

        return [.. entries.Values];
    }
}