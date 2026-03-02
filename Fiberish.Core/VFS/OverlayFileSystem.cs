using Fiberish.Native;
using Microsoft.Extensions.Logging;
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

    public int CopyUp(LinuxFile? linuxFile)
    {
        if (UpperDentry != null) return 0;
        if (LowerDentry == null) throw new InvalidOperationException("No lower dentry to copy up");

        // 1. Ensure parent directories exist in upper FS
        var upperParent = EnsureParentUpper(LowerDentry);

        var osb = (OverlaySuperBlock)SuperBlock;
        var upperDentry = new Dentry(LowerDentry.Name, null, upperParent, osb.UpperSB);

        if (Type == InodeType.Directory)
        {
            // Check if directory already exists in upper before creating a duplicate.
            // This can happen when multiple OverlayInode instances for the same directory
            // each try to CopyUp independently.
            var existing = upperParent.Inode!.Lookup(LowerDentry.Name);
            if (existing != null)
            {
                UpperDentry = existing;
                return 0;
            }
            upperParent.Inode!.Mkdir(upperDentry, Mode, Uid, Gid);
            UpperDentry = upperDentry;
            return 0;
        }
        else
        {
            upperParent.Inode!.Create(upperDentry, Mode, Uid, Gid);

            // 3. Copy data
            if (LowerInode != null)
            {
                try
                {
                    var buf = new byte[4096];
                    long pos = 0;
                    while (true)
                    {
                        // Use null to trigger host-internal read without dependency on user's open mode
                        var n = LowerInode.Read(null!, buf, pos);
                        if (n <= 0) break;
                        upperDentry.Inode!.Write(null!, buf.AsSpan(0, n), pos);
                        pos += n;
                    }
                }
                catch (Exception ex)
                {
                    Fiberish.Diagnostics.Logging.CreateLogger<OverlayInode>().LogWarning("CopyUp failed for {Name}: {Error}", LowerDentry.Name, ex.Message);
                    // Cleanup failed upper dentry if needed? For now just return error.
                    return -(int)Errno.EACCES;
                }
            }

            UpperDentry = upperDentry;

            // 4. Redirect handle if provided
            if (linuxFile != null)
            {
                LowerInode!.Release(linuxFile);
                linuxFile.PrivateData = null; // Ensure clean state
                UpperInode!.Open(linuxFile);
            }

            return 0;
        }
    }

    private Dentry EnsureParentUpper(Dentry lowerDentry)
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        var parentLower = lowerDentry.Parent;

        if (parentLower == null || parentLower == lowerDentry || parentLower.Name == "/")
            return osb.UpperSB.Root;

        // Recursively ensure parent's parent
        var upperParentOfParent = EnsureParentUpper(parentLower);

        // Does the parent exist in the upper parent?
        var existing = upperParentOfParent.Inode!.Lookup(parentLower.Name);
        if (existing != null) return existing;

        // Must create parent directory in upper
        var newUpperParent = new Dentry(parentLower.Name, null, upperParentOfParent, osb.UpperSB);
        upperParentOfParent.Inode!.Mkdir(newUpperParent, parentLower.Inode!.Mode, parentLower.Inode.Uid, parentLower.Inode.Gid);
        return newUpperParent;
    }

    public override Dentry? Lookup(string name)
    {
        if (name == "..") return null; // Handled by VFS
        if (name == ".") return null; // Handled by VFS

        // 1. Lookup in Upper
        var upperDentry = UpperInode?.Lookup(name);

        // 2. Lookup in Lower
        var lowerDentry = LowerInode?.Lookup(name);

        if (upperDentry == null && lowerDentry == null) return null;

        // Create Overlay Inode
        var inode = new OverlayInode(SuperBlock, lowerDentry, upperDentry);

        var parentDentry = Dentries.Count > 0 ? Dentries[0] : null;
        return new Dentry(name, inode, parentDentry, SuperBlock);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        // Create in Upper.
        if (UpperDentry == null)
            CopyUpDirectory();

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
        if (UpperDentry == null)
            CopyUpDirectory();

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode!.Mkdir(upperDentry, mode, uid, gid);

        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry);
        dentry.Instantiate(newOverlayInode);

        return dentry;
    }

    /// <summary>
    /// Copy-up a lower-only directory to the upper FS.
    /// Creates an empty directory in the upper FS with the same mode/uid/gid.
    /// Does NOT copy children — they remain in the lower layer and are merged via Lookup.
    /// </summary>
    private void CopyUpDirectory()
    {
        if (UpperDentry != null) return;
        if (LowerDentry == null)
            throw new InvalidOperationException("Cannot copy-up: no lower dentry");

        UpperDentry = EnsureUpperDir(LowerDentry);
    }

    private Dentry EnsureUpperDir(Dentry lowerDentry)
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        if (lowerDentry.Parent == null || lowerDentry.Parent == lowerDentry)
            return osb.UpperSB.Root;

        var upperParent = EnsureParentUpper(lowerDentry);
        var existing = upperParent.Inode!.Lookup(lowerDentry.Name);
        if (existing != null) return existing;

        var newUpper = new Dentry(lowerDentry.Name, null, upperParent, osb.UpperSB);
        upperParent.Inode!.Mkdir(newUpper, lowerDentry.Inode!.Mode, lowerDentry.Inode.Uid, lowerDentry.Inode.Gid);
        return newUpper;
    }

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        if (UpperInode != null) return UpperInode.Flock(linuxFile, operation);
        if (LowerInode != null) return LowerInode.Flock(linuxFile, operation);
        return -(int)Errno.ENOSYS;
    }

    public override string Readlink()
    {
        if (UpperInode != null && UpperInode.Type == InodeType.Symlink)
            return UpperInode.Readlink();
        if (LowerInode != null && LowerInode.Type == InodeType.Symlink)
            return LowerInode.Readlink();
        throw new InvalidOperationException("Not a symlink");
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

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (newParent is not OverlayInode targetParent)
            throw new InvalidOperationException("Target parent is not overlay inode");

        // Rename mutates directory entries, so parents must exist in upper.
        if (UpperDentry == null)
            CopyUpDirectory();
        if (targetParent.UpperDentry == null)
            targetParent.CopyUpDirectory();

        if (UpperInode == null || targetParent.UpperInode == null)
            throw new InvalidOperationException("Upper directory is unavailable for rename");

        // Common apk path: source is newly created temp file in upper already.
        // For lower-only source entries, full copy-up rename semantics can be added later.
        UpperInode.Rename(oldName, targetParent.UpperInode, newName);
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        if (oldInode is not OverlayInode oldOverlay)
            throw new InvalidOperationException("Source is not an overlay inode");

        // Link mutates directory entries, so parent must exist in upper
        if (UpperDentry == null)
            CopyUpDirectory();

        // Source must also be evaluated. If it only exists in lower, it needs to be copied up
        // because we can't create a hardlink in upper pointing to lower.
        // Inoverlayfs, a hardlink to a lower file triggers copy-up of the source.
        if (oldOverlay.UpperInode == null)
        {
            var res = oldOverlay.CopyUp(null);
            if (res < 0)
                throw new IOException($"CopyUp failed during Link with error {res}");
        }

        if (UpperInode == null || oldOverlay.UpperInode == null)
            throw new InvalidOperationException("Upper directory or source is unavailable for link");

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode.Link(upperDentry, oldOverlay.UpperInode);

        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry);
        dentry.Instantiate(newOverlayInode);

        return dentry;
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
        if (UpperInode == null)
        {
            var res = CopyUp(linuxFile);
            if (res < 0) return res;
        }

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
        if (LowerInode == null) return -(int)Errno.EROFS;

        var res = CopyUp(null);
        if (res < 0) return res;
        return UpperInode!.Truncate(size);
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
