using System.Runtime.CompilerServices;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

// needed for SyscallManager Access if required, but maybe not directly here yet

namespace Fiberish.VFS;

public class OverlayFileSystem : FileSystem
{
    public OverlayFileSystem(DeviceNumberManager? devManager = null) : base(devManager)
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

        var lowers = options.Lowers?.Where(l => l != null).ToList() ?? [];
        if (lowers.Count == 0 && options.Lower != null) lowers.Add(options.Lower);
        if (lowers.Count == 0 && options.LowerRoots != null)
            lowers.AddRange(options.LowerRoots.Select(r => r.SuperBlock).Where(sb => sb != null).Distinct());
        if (lowers.Count == 0)
            throw new ArgumentException("OverlayFS requires at least one lower superblock");
        var upper = options.Upper ?? options.UpperRoot?.SuperBlock;
        if (upper == null)
            throw new ArgumentException("OverlayFS requires an upper layer");

        var sb = new OverlaySuperBlock(fsType, lowers, upper, DevManager);
        var lowerRoots = options.LowerRoots?.Where(r => r != null).ToList() ??
                         lowers.Select(l => l.Root).Where(r => r != null).ToList();
        var upperRoot = options.UpperRoot ?? upper.Root;
        sb.Root = new Dentry("/", new OverlayInode(sb, lowerRoots!, upperRoot), null, sb);
        sb.Root.Parent = sb.Root;

        return sb;
    }
}

public class OverlayMountOptions
{
    public SuperBlock? Lower { get; set; }
    public IReadOnlyList<SuperBlock>? Lowers { get; set; }
    public SuperBlock? Upper { get; set; }
    public IReadOnlyList<Dentry>? LowerRoots { get; set; }
    public Dentry? UpperRoot { get; set; }
}

public readonly record struct InodeKey(uint Dev, ulong Ino);

public class OverlaySuperBlock : SuperBlock
{
    private readonly Dictionary<InodeKey, OverlayFileLockState> _flocks = new();
    private readonly object _flockSync = new();
    private readonly Dictionary<OverlayNodeStateKey, OverlayNodeState> _nodeStates = new();
    private readonly HashSet<InodeKey> _opaqueDirs = new();
    private readonly Dictionary<InodeKey, HashSet<string>> _whiteouts = new();

    public OverlaySuperBlock(FileSystemType type, IReadOnlyList<SuperBlock> lowers, SuperBlock upper,
        DeviceNumberManager? devManager = null) : base(devManager)
    {
        if (lowers.Count == 0)
            throw new ArgumentException("OverlayFS requires at least one lower layer", nameof(lowers));
        Type = type;
        LowerSBs = lowers;
        LowerSB = lowers[0];
        UpperSB = upper;
        WhiteoutCodec = new HybridWhiteoutCodec();
    }

    public IReadOnlyList<SuperBlock> LowerSBs { get; }
    public SuperBlock LowerSB { get; }
    public SuperBlock UpperSB { get; }
    public IOverlayWhiteoutCodec WhiteoutCodec { get; }

    public OverlayNodeState GetOrCreateNodeState(IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        if ((lowers == null || lowers.Count == 0) && upper == null)
            throw new InvalidOperationException("Overlay node state requires at least one backing dentry");

        var key = OverlayNodeStateKey.Create(lowers, upper);
        lock (Lock)
        {
            if (!_nodeStates.TryGetValue(key, out var state))
            {
                state = new OverlayNodeState(lowers, upper);
                _nodeStates[key] = state;
                return state;
            }

            state.UpdateBackings(lowers, upper);
            return state;
        }
    }

    public void AddWhiteout(InodeKey dirKey, string name)
    {
        lock (Lock)
        {
            if (!_whiteouts.TryGetValue(dirKey, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                _whiteouts[dirKey] = names;
            }

            names.Add(name);
        }
    }

    public void RemoveWhiteout(InodeKey dirKey, string name)
    {
        lock (Lock)
        {
            if (_whiteouts.TryGetValue(dirKey, out var names))
                names.Remove(name);
        }
    }

    public bool HasWhiteout(InodeKey dirKey, string name)
    {
        lock (Lock)
        {
            return _whiteouts.TryGetValue(dirKey, out var names) && names.Contains(name);
        }
    }

    public IReadOnlyCollection<string> GetWhiteouts(InodeKey dirKey)
    {
        lock (Lock)
        {
            if (!_whiteouts.TryGetValue(dirKey, out var names)) return Array.Empty<string>();
            return names.ToArray();
        }
    }

    public void MarkOpaque(InodeKey dirKey)
    {
        lock (Lock)
        {
            _opaqueDirs.Add(dirKey);
        }
    }

    public bool IsOpaque(InodeKey dirKey)
    {
        lock (Lock)
        {
            return _opaqueDirs.Contains(dirKey);
        }
    }

    public override Inode AllocInode()
    {
        // Only called if we need a pure virtual inode? 
        // Overlay inodes are always bonded to underlying inodes.
        // But we might need to allocate a new OverlayInode wrapper.
        return new OverlayInode(this, (Dentry?)null, null);
    }

    public int Flock(InodeKey fileKey, LinuxFile linuxFile, int operation)
    {
        var nonBlock = (operation & LinuxConstants.LOCK_NB) != 0;
        var op = operation & ~LinuxConstants.LOCK_NB;

        lock (_flockSync)
        {
            while (true)
            {
                _flocks.TryGetValue(fileKey, out var state);

                if (op == LinuxConstants.LOCK_UN)
                {
                    if (state == null) return 0;
                    if (state.ExclusiveHolder == linuxFile) state.ExclusiveHolder = null;
                    state.SharedHolders.Remove(linuxFile);
                    if (state.ExclusiveHolder == null && state.SharedHolders.Count == 0)
                    {
                        state.LockType = 0;
                        _flocks.Remove(fileKey);
                    }

                    Monitor.PulseAll(_flockSync);
                    return 0;
                }

                state ??= CreateLockState(fileKey);

                var canAcquire = false;
                if (op == LinuxConstants.LOCK_SH)
                {
                    if (state.ExclusiveHolder == null || state.ExclusiveHolder == linuxFile) canAcquire = true;
                }
                else if (op == LinuxConstants.LOCK_EX)
                {
                    if (state.LockType == 0) canAcquire = true;
                    else if (state.ExclusiveHolder == linuxFile) canAcquire = true;
                    else if (state.SharedHolders.Count == 1 &&
                             state.SharedHolders.Contains(linuxFile) &&
                             state.ExclusiveHolder == null)
                        canAcquire = true;
                }
                else
                {
                    return -(int)Errno.EINVAL;
                }

                if (canAcquire)
                {
                    if (op == LinuxConstants.LOCK_SH)
                    {
                        if (state.ExclusiveHolder == linuxFile) state.ExclusiveHolder = null;
                        state.SharedHolders.Add(linuxFile);
                        state.LockType = 1;
                    }
                    else
                    {
                        state.SharedHolders.Remove(linuxFile);
                        state.ExclusiveHolder = linuxFile;
                        state.LockType = 2;
                    }

                    return 0;
                }

                if (nonBlock) return -(int)Errno.EAGAIN;
                Monitor.Wait(_flockSync);
            }
        }
    }

    private OverlayFileLockState CreateLockState(InodeKey fileKey)
    {
        var state = new OverlayFileLockState();
        _flocks[fileKey] = state;
        return state;
    }

    private sealed class OverlayFileLockState
    {
        public int LockType { get; set; }
        public HashSet<LinuxFile> SharedHolders { get; } = [];
        public LinuxFile? ExclusiveHolder { get; set; }
    }
}

public readonly record struct OverlayNodeStateKey(int Identity)
{
    public static OverlayNodeStateKey Create(IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        var identity = lowers is { Count: > 0 }
            ? RuntimeHelpers.GetHashCode(lowers[0])
            : RuntimeHelpers.GetHashCode(upper!);
        return new OverlayNodeStateKey(identity);
    }
}

public sealed class OverlayNodeState
{
    private readonly HashSet<OverlayInode> _aliases = [];
    private readonly Dictionary<string, OverlayNodeState> _children = new(StringComparer.Ordinal);
    private List<Dentry> _lowerDentries;

    public OverlayNodeState(IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        _lowerDentries = lowers?.Where(d => d != null).ToList() ?? [];
        UpperDentry = upper;
    }

    public object SyncRoot { get; } = new();
    public IReadOnlyList<Dentry> LowerDentries => _lowerDentries;
    public Dentry? UpperDentry { get; private set; }

    public void UpdateBackings(IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        lock (SyncRoot)
        {
            var shouldReplaceUpper = upper != null &&
                                     UpperDentry != null &&
                                     !ReferenceEquals(UpperDentry, upper) &&
                                     IsStaleUpperDentry(UpperDentry);

            if (shouldReplaceUpper)
                UpperDentry = upper;

            if (_lowerDentries.Count == 0 && lowers is { Count: > 0 })
                _lowerDentries = lowers.Where(d => d != null).ToList();
            if (UpperDentry == null && upper != null)
                UpperDentry = upper;
        }
    }

    public void SetUpperDentry(Dentry upper)
    {
        lock (SyncRoot)
        {
            UpperDentry = upper;
        }
    }

    public void RegisterAlias(OverlayInode inode)
    {
        lock (SyncRoot)
        {
            _aliases.Add(inode);
        }
    }

    public void UnregisterAlias(OverlayInode inode)
    {
        lock (SyncRoot)
        {
            _aliases.Remove(inode);
        }
    }

    public OverlayInode[] SnapshotAliases()
    {
        lock (SyncRoot)
        {
            return [.. _aliases];
        }
    }

    public OverlayNodeState GetOrCreateChildState(string name, IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        lock (SyncRoot)
        {
            if (!_children.TryGetValue(name, out var state))
            {
                state = new OverlayNodeState(lowers, upper);
                _children[name] = state;
                return state;
            }

            state.UpdateBackings(lowers, upper);
            return state;
        }
    }

    public bool TryRemoveChildState(string name, OverlayNodeState expected)
    {
        lock (SyncRoot)
        {
            return _children.TryGetValue(name, out var state) &&
                   ReferenceEquals(state, expected) &&
                   _children.Remove(name);
        }
    }

    public void AttachChildState(string name, OverlayNodeState state, IReadOnlyList<Dentry>? lowers, Dentry? upper)
    {
        lock (SyncRoot)
        {
            state.UpdateBackings(lowers, upper);
            _children[name] = state;
        }
    }

    private static bool IsStaleUpperDentry(Dentry upper)
    {
        return upper.Inode == null;
    }
}

public class OverlayInode : MappingBackedInode
{
    private readonly Dictionary<LinuxFile, Inode> _openBackingByFile = [];
    private readonly OverlayNodeState _state;

    public OverlayInode(SuperBlock sb, Dentry? lower, Dentry? upper)
        : this(sb, lower != null ? [lower] : null, upper, null)
    {
    }

    public OverlayInode(SuperBlock sb, IReadOnlyList<Dentry>? lowers, Dentry? upper)
        : this(sb, lowers, upper, null)
    {
    }

    private OverlayInode(SuperBlock sb, IReadOnlyList<Dentry>? lowers, Dentry? upper, OverlayNodeState? state)
    {
        SuperBlock = sb;
        _state = state ?? ((lowers == null || lowers.Count == 0) && upper == null
            ? new OverlayNodeState(null, null)
            : ((OverlaySuperBlock)sb).GetOrCreateNodeState(lowers, upper));
        _state.RegisterAlias(this);
        InitializeOverlayLinkCount("OverlayInode.ctor");
    }

    private Inode? SourceInode => UpperInode ?? LowerInode ?? GetAnyOpenBackingInode();
    public override bool SupportsMmap => Type == InodeType.File && (SourceInode?.SupportsMmap ?? false);

    public override ulong Ino
    {
        get => SourceInode?.Ino ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Ino = value;
        }
    }

    public override uint Dev => SourceInode?.Dev ?? base.Dev;

    public override InodeType Type
    {
        get => SourceInode?.Type ?? InodeType.File;
        set
        {
            if (SourceInode != null) SourceInode.Type = value;
        }
    }

    public override int Mode
    {
        get => SourceInode?.Mode ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Mode = value;
        }
    }

    public override uint Rdev
    {
        get => SourceInode?.Rdev ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Rdev = value;
        }
    }

    public override int Uid
    {
        get => SourceInode?.Uid ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Uid = value;
        }
    }

    public override int Gid
    {
        get => SourceInode?.Gid ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Gid = value;
        }
    }

    public override ulong Size
    {
        get => SourceInode?.Size ?? 0;
        set
        {
            if (SourceInode != null) SourceInode.Size = value;
        }
    }

    public override DateTime MTime
    {
        get => SourceInode?.MTime ?? DateTime.UnixEpoch;
        set
        {
            if (SourceInode != null) SourceInode.MTime = value;
        }
    }

    public override DateTime ATime
    {
        get => SourceInode?.ATime ?? DateTime.UnixEpoch;
        set
        {
            if (SourceInode != null) SourceInode.ATime = value;
        }
    }

    public override DateTime CTime
    {
        get => SourceInode?.CTime ?? DateTime.UnixEpoch;
        set
        {
            if (SourceInode != null) SourceInode.CTime = value;
        }
    }

    /// <summary>
    ///     Lower dentries ordered from top-most lower layer to bottom-most lower layer.
    ///     Lookup resolves in this order.
    /// </summary>
    public IReadOnlyList<Dentry> LowerDentries => _state.LowerDentries;

    public Dentry? LowerDentry => LowerDentries.Count > 0 ? LowerDentries[0] : null;
    public Dentry? UpperDentry => _state.UpperDentry;

    public Inode? LowerInode => LowerDentry?.Inode;
    public Inode? UpperInode => UpperDentry?.Inode;

    public override uint GetLinkCountForStat()
    {
        if (Type == InodeType.Directory)
            return (uint)Math.Max(0, ComputeMergedDirectoryLinkCount());

        return SourceInode?.GetLinkCountForStat() ?? base.GetLinkCountForStat();
    }

    private static InodeRefKind GetBackingRefKind(LinuxFile linuxFile)
    {
        return linuxFile.Kind == LinuxFile.ReferenceKind.MmapHold
            ? InodeRefKind.FileMmap
            : InodeRefKind.FileOpen;
    }

    private Inode? GetAnyOpenBackingInode()
    {
        return _openBackingByFile.Count == 0 ? null : _openBackingByFile.Values.FirstOrDefault();
    }

    private Inode? ResolveSourceForFile(LinuxFile? linuxFile)
    {
        if (UpperInode != null)
            return UpperInode;
        if (linuxFile != null && _openBackingByFile.TryGetValue(linuxFile, out var bound))
            return bound;
        if (LowerInode != null)
            return LowerInode;
        return GetAnyOpenBackingInode();
    }

    private Inode? ResolvePagingSource(LinuxFile? linuxFile)
    {
        return ResolveSourceForFile(linuxFile);
    }

    internal Inode? ResolveMmapSource(LinuxFile? linuxFile)
    {
        return ResolvePagingSource(linuxFile);
    }

    private int EnsureWritableBacking(LinuxFile? linuxFile)
    {
        if (UpperInode == null && LowerInode != null)
            return CopyUp(linuxFile);
        return 0;
    }

    private void BindFileBacking(LinuxFile linuxFile, Inode backing, string reason)
    {
        if (_openBackingByFile.TryGetValue(linuxFile, out var existing))
        {
            if (ReferenceEquals(existing, backing))
                return;
            UnbindFileBacking(linuxFile, $"{reason}.replace");
            linuxFile.PrivateData = null;
        }

        var refKind = GetBackingRefKind(linuxFile);
        backing.AcquireRef(refKind, reason);
        VfsFileHolderTracking.Register(backing, linuxFile, reason);
        backing.Open(linuxFile);
        _openBackingByFile[linuxFile] = backing;
    }

    private void UnbindFileBacking(LinuxFile linuxFile, string reason)
    {
        if (!_openBackingByFile.Remove(linuxFile, out var backing))
            return;

        backing.Release(linuxFile);
        VfsFileHolderTracking.Unregister(backing, linuxFile);
        backing.ReleaseRef(GetBackingRefKind(linuxFile), reason);
    }

    private void RebindFileBacking(LinuxFile linuxFile, Inode backing, string reason)
    {
        if (_openBackingByFile.TryGetValue(linuxFile, out var existing) && ReferenceEquals(existing, backing))
            return;

        UnbindFileBacking(linuxFile, $"{reason}.old");
        linuxFile.PrivateData = null;
        BindFileBacking(linuxFile, backing, $"{reason}.new");
    }

    private void RebindAllFileBackings(Inode backing, string reason)
    {
        if (_openBackingByFile.Count == 0)
            return;

        foreach (var linuxFile in _openBackingByFile.Keys.ToArray())
            RebindFileBacking(linuxFile, backing, reason);
    }

    private void RefreshOverlayLinkCountFromSource(string reason)
    {
        InitializeOverlayLinkCount(reason);
    }

    public int CopyUp(LinuxFile? linuxFile)
    {
        lock (_state.SyncRoot)
        {
            if (UpperInode != null) return 0;
            if (LowerDentry == null) return -(int)Errno.ENOENT;

            var ensureParentRc = EnsureParentUpper(LowerDentry, out var upperParent);
            if (ensureParentRc < 0)
                return ensureParentRc;
            var copyRc = CopyUpToUpper(upperParent, LowerDentry.Name, linuxFile);
            if (copyRc < 0)
                return copyRc;
        }

        return 0;
    }

    private int CopyUpToCurrentParent(string currentName, OverlayInode currentParent, LinuxFile? linuxFile)
    {
        lock (_state.SyncRoot)
        {
            if (UpperInode != null)
                return 0;
            if (currentParent.UpperDentry == null)
            {
                var copyParentRc = currentParent.CopyUpDirectory();
                if (copyParentRc < 0)
                    return copyParentRc;
            }

            if (currentParent.UpperDentry == null)
                return -(int)Errno.EIO;

            return CopyUpToUpper(currentParent.UpperDentry, currentName, linuxFile);
        }
    }

    private int CopyUpToUpper(Dentry upperParent, string upperName, LinuxFile? linuxFile)
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        var upperDentry = new Dentry(upperName, null, upperParent, osb.UpperSB);

        if (Type == InodeType.Directory)
        {
            var existing = upperParent.Inode!.Lookup(upperName);
            if (existing != null)
            {
                _state.SetUpperDentry(existing);
                PromoteStateBackings(existing.Inode, linuxFile);
                return 0;
            }

            var mkdirRc = upperParent.Inode!.Mkdir(upperDentry, Mode, Uid, Gid);
            if (mkdirRc < 0)
                return mkdirRc;
            _state.SetUpperDentry(upperDentry);
            PromoteStateBackings(UpperInode, linuxFile);
            return 0;
        }

        var lowerInode = LowerInode;
        var copyPayloadRc = 0;
        switch (Type)
        {
            case InodeType.Symlink:
                if (lowerInode == null)
                    return -(int)Errno.ENOENT;
                var readlinkRc = lowerInode.Readlink(out var linkTarget);
                if (readlinkRc < 0)
                    return readlinkRc;
                copyPayloadRc = upperParent.Inode!.Symlink(upperDentry, linkTarget!, Uid, Gid);
                break;

            case InodeType.CharDev:
            case InodeType.BlockDev:
            case InodeType.Fifo:
            case InodeType.Socket:
                copyPayloadRc = upperParent.Inode!.Mknod(upperDentry, Mode, Uid, Gid, Type, Rdev);
                break;

            default:
            {
                var existing = upperParent.Inode!.Lookup(upperName);
                if (existing != null)
                {
                    _state.SetUpperDentry(existing);
                    PromoteStateBackings(existing.Inode, linuxFile);
                    return 0;
                }

                copyPayloadRc = upperParent.Inode!.Create(upperDentry, Mode, Uid, Gid);
                if (copyPayloadRc < 0)
                    break;

                if (lowerInode != null)
                {
                    LinuxFile? copyFile = null;
                    if (linuxFile == null && LowerDentry != null)
                    {
                        copyFile = new LinuxFile(LowerDentry, FileFlags.O_RDONLY, null!);
                        lowerInode.Open(copyFile);
                    }

                    var buf = new byte[4096];
                    long pos = 0;
                    try
                    {
                        while (true)
                        {
                            var n = lowerInode.ReadToHost(null, copyFile ?? linuxFile!, buf, pos);
                            if (n < 0)
                            {
                                copyPayloadRc = n;
                                break;
                            }

                            if (n == 0) break;
                            var writeRc = upperDentry.Inode!.WriteFromHost(null, null!, buf.AsSpan(0, n), pos);
                            if (writeRc != n)
                            {
                                copyPayloadRc = writeRc < 0 ? writeRc : -(int)Errno.EIO;
                                break;
                            }

                            pos += n;
                        }
                    }
                    finally
                    {
                        copyFile?.Close();
                    }
                }

                break;
            }
        }

        if (copyPayloadRc < 0)
        {
            _ = upperParent.Inode!.Unlink(upperName);
            Logging.CreateLogger<OverlayInode>()
                .LogWarning("CopyUp failed for {Name}: rc={Result}", upperName, copyPayloadRc);
            return copyPayloadRc;
        }

        _state.SetUpperDentry(upperDentry);
        PromoteStateBackings(UpperInode, linuxFile);
        return 0;
    }

    private void PromoteStateBackings(Inode? newBacking, LinuxFile? triggeringFile = null)
    {
        if (newBacking == null) return;
        foreach (var alias in _state.SnapshotAliases())
        {
            alias.RebindAllFileBackings(newBacking, "OverlayInode.CopyUp.rebind-all");
            ProcessAddressSpaceSync.MigrateOverlayMappings(alias, newBacking);
        }

        if (triggeringFile != null)
            RebindFileBacking(triggeringFile, newBacking, "OverlayInode.CopyUp");
    }

    private int EnsureParentUpper(Dentry lowerDentry, out Dentry upperParent)
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        var parentLower = lowerDentry.Parent;

        if (parentLower == null || parentLower == lowerDentry || parentLower.Name == "/")
        {
            upperParent = osb.UpperSB.Root;
            return 0;
        }

        // Recursively ensure parent's parent
        var parentRc = EnsureParentUpper(parentLower, out var upperParentOfParent);
        if (parentRc < 0)
        {
            upperParent = null!;
            return parentRc;
        }

        // Does the parent exist in the upper parent?
        var existing = upperParentOfParent.Inode!.Lookup(parentLower.Name);
        if (existing != null)
        {
            upperParent = existing;
            return 0;
        }

        // Must create parent directory in upper
        var newUpperParent = new Dentry(parentLower.Name, null, upperParentOfParent, osb.UpperSB);
        var mkdirRc = upperParentOfParent.Inode!.Mkdir(newUpperParent, parentLower.Inode!.Mode, parentLower.Inode.Uid,
            parentLower.Inode.Gid);
        if (mkdirRc < 0)
        {
            upperParent = null!;
            return mkdirRc;
        }

        upperParent = newUpperParent;
        return 0;
    }

    public override Dentry? Lookup(string name)
    {
        if (name == "..") return null; // Handled by VFS
        if (name == ".") return null; // Handled by VFS
        var osb = (OverlaySuperBlock)SuperBlock;
        if (osb.WhiteoutCodec.IsInternalMarkerName(name)) return null;

        var dirKey = new InodeKey(Dev, Ino);
        if (osb.HasWhiteout(dirKey, name) || osb.WhiteoutCodec.HasEncodedWhiteout(UpperInode, name)) return null;

        // 1. Lookup in Upper
        var upperDentry = UpperInode?.Lookup(name);
        if (upperDentry != null && osb.WhiteoutCodec.IsWhiteoutInode(upperDentry.Inode))
            return null;

        var dirOpaque = osb.IsOpaque(dirKey) || osb.WhiteoutCodec.IsEncodedOpaque(UpperInode);
        if (dirOpaque && upperDentry == null) return null;

        // 2. Lookup in Lower layers (top-most lower wins)
        Dentry? lowerDentry = null;
        foreach (var lower in LowerDentries)
        {
            var candidate = lower.Inode?.Lookup(name);
            if (candidate != null)
            {
                lowerDentry = candidate;
                break;
            }
        }

        if (upperDentry == null && lowerDentry == null) return null;

        // Create Overlay Inode
        var state = _state.GetOrCreateChildState(name, lowerDentry != null ? [lowerDentry] : null, upperDentry);
        var inode = new OverlayInode(SuperBlock, lowerDentry != null ? [lowerDentry] : null, upperDentry, state);

        var parentDentry = Dentries.Count > 0 ? Dentries[0] : null;
        return new Dentry(name, inode, parentDentry, SuperBlock);
    }

    public override int Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Lookup(dentry.Name) != null)
            return -(int)Errno.EEXIST;

        // Create in Upper.
        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        // Delegate to Upper
        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        var createRc = UpperInode!.Create(upperDentry, mode, uid, gid);
        if (createRc < 0)
            return createRc;

        // Now update the overlay dentry's inode
        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState); // Created only in upper
        dentry.Instantiate(newOverlayInode);

        return 0;
    }

    public override int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (Lookup(dentry.Name) != null)
            return -(int)Errno.EEXIST;

        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        var mkdirRc = UpperInode!.Mkdir(upperDentry, mode, uid, gid);
        if (mkdirRc < 0)
            return mkdirRc;

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        newOverlayInode.RefreshOverlayLinkCountFromSource("OverlayInode.Mkdir.child-refresh");
        RefreshOverlayLinkCountFromSource("OverlayInode.Mkdir.parent-refresh");

        return 0;
    }

    /// <summary>
    ///     Copy-up a lower-only directory to the upper FS.
    ///     Creates an empty directory in the upper FS with the same mode/uid/gid.
    ///     Does NOT copy children — they remain in the lower layer and are merged via Lookup.
    /// </summary>
    private int CopyUpDirectory()
    {
        if (UpperDentry != null) return 0;
        if (LowerDentry == null)
            return -(int)Errno.ENOENT;

        var ensureRc = EnsureUpperDir(LowerDentry, out var upperDir);
        if (ensureRc < 0)
            return ensureRc;

        _state.SetUpperDentry(upperDir);
        return 0;
    }

    private int EnsureUpperDir(Dentry lowerDentry, out Dentry upperDir)
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        if (lowerDentry.Parent == null || lowerDentry.Parent == lowerDentry)
        {
            upperDir = osb.UpperSB.Root;
            return 0;
        }

        var parentRc = EnsureParentUpper(lowerDentry, out var upperParent);
        if (parentRc < 0)
        {
            upperDir = null!;
            return parentRc;
        }

        var existing = upperParent.Inode!.Lookup(lowerDentry.Name);
        if (existing != null)
        {
            upperDir = existing;
            return 0;
        }

        var newUpper = new Dentry(lowerDentry.Name, null, upperParent, osb.UpperSB);
        var mkdirRc = upperParent.Inode!.Mkdir(newUpper, lowerDentry.Inode!.Mode, lowerDentry.Inode.Uid,
            lowerDentry.Inode.Gid);
        if (mkdirRc < 0)
        {
            upperDir = null!;
            return mkdirRc;
        }

        upperDir = newUpper;
        return 0;
    }

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        if (UpperInode != null) return UpperInode.Flock(linuxFile, operation);
        if (LowerInode != null)
        {
            var rc = LowerInode.Flock(linuxFile, operation);
            if (rc != -(int)Errno.ENOSYS) return rc;
            return ((OverlaySuperBlock)SuperBlock).Flock(new InodeKey(Dev, Ino), linuxFile, operation);
        }

        return -(int)Errno.ENOSYS;
    }

    public override int Readlink(out string? target)
    {
        if (UpperInode != null && UpperInode.Type == InodeType.Symlink)
            return UpperInode.Readlink(out target);
        if (LowerInode != null && LowerInode.Type == InodeType.Symlink)
            return LowerInode.Readlink(out target);
        target = null;
        return -(int)Errno.EINVAL;
    }

    public override int Unlink(string name)
    {
        var overlayEntry = Lookup(name);
        if (overlayEntry == null)
            return -(int)Errno.ENOENT;
        if (overlayEntry.Inode?.Type == InodeType.Directory)
            return -(int)Errno.EISDIR;

        var inUpper = UpperInode?.Lookup(name) != null;
        var inLower = LookupInAnyLower(name) != null;
        var osb = (OverlaySuperBlock)SuperBlock;

        if (inUpper)
        {
            var unlinkRc = UpperInode!.Unlink(name);
            if (unlinkRc < 0)
                return unlinkRc;
        }

        if (inLower)
        {
            if (UpperDentry == null)
            {
                var copyRc = CopyUpDirectory();
                if (copyRc < 0)
                    return copyRc;
            }

            osb.AddWhiteout(new InodeKey(Dev, Ino), name);
            osb.WhiteoutCodec.TryCreateEncodedWhiteout(this, name);
        }

        if (overlayEntry.Inode is OverlayInode overlayChild)
        {
            overlayChild.RefreshOverlayLinkCountFromSource("OverlayInode.Unlink.child-refresh");
            DetachRemovedChildState(name, overlayEntry);
        }

        return 0;
    }

    public override int Rmdir(string name)
    {
        var overlayEntry = Lookup(name);
        if (overlayEntry == null)
            return -(int)Errno.ENOENT;
        if (overlayEntry.Inode?.Type != InodeType.Directory)
            return -(int)Errno.ENOTDIR;
        if (overlayEntry.Inode.GetEntries().Any(e => e.Name is not "." and not ".."))
            return -(int)Errno.ENOTEMPTY;

        var inUpper = UpperInode?.Lookup(name) != null;
        var inLower = LookupInAnyLower(name) != null;
        var osb = (OverlaySuperBlock)SuperBlock;
        if (inUpper)
        {
            if (UpperInode!.Lookup(name) is { Inode: { } upperDirInode } upperDir)
            {
                var cleanupRc = RemoveUpperOverlayInternalEntries(upperDir);
                if (cleanupRc < 0)
                    return cleanupRc;
                if (upperDirInode.GetEntries().Any(e => e.Name is not "." and not ".."))
                    return -(int)Errno.ENOTEMPTY;
            }

            var rmdirRc = UpperInode.Rmdir(name);
            if (rmdirRc < 0)
                return rmdirRc;
        }

        if (inLower)
        {
            if (UpperDentry == null)
            {
                var copyRc = CopyUpDirectory();
                if (copyRc < 0)
                    return copyRc;
            }

            if (UpperInode?.Lookup(name) is { } upperDir &&
                upperDir.Inode?.GetEntries().Any(e => e.Name is not "." and not "..") == true)
                return -(int)Errno.ENOTEMPTY;
            osb.AddWhiteout(new InodeKey(Dev, Ino), name);
            osb.WhiteoutCodec.TryCreateEncodedWhiteout(this, name);
        }

        RefreshOverlayLinkCountFromSource("OverlayInode.Rmdir.parent-refresh");
        DetachRemovedChildState(name, overlayEntry);
        return 0;
    }

    private bool DetachRemovedChildState(string name, Dentry overlayEntry)
    {
        if (overlayEntry.Inode is not OverlayInode overlayChild)
            return false;

        // Removing the namespace mapping lets a later recreate of the same name
        // get a fresh child state without retargeting still-open aliases.
        return _state.TryRemoveChildState(name, overlayChild._state);
    }

    private int RemoveUpperOverlayInternalEntries(Dentry upperDir)
    {
        var upperDirInode = upperDir.Inode;
        if (upperDirInode == null) return 0;

        var osb = (OverlaySuperBlock)SuperBlock;
        foreach (var entry in upperDirInode.GetEntries().Where(e => e.Name is not "." and not "..").ToList())
        {
            var child = upperDirInode.Lookup(entry.Name);
            if (!osb.WhiteoutCodec.IsEncodedOpaqueEntry(entry) &&
                !osb.WhiteoutCodec.TryDecodeEncodedWhiteout(entry, child?.Inode, out _))
                continue;

            var unlinkRc = upperDirInode.Unlink(entry.Name);
            if (unlinkRc < 0)
                return unlinkRc;
        }

        return 0;
    }

    public override int Rename(string oldName, Inode newParent, string newName)
    {
        if (newParent is not OverlayInode targetParent)
            return -(int)Errno.EXDEV;
        if (string.Equals(oldName, ".", StringComparison.Ordinal) ||
            string.Equals(oldName, "..", StringComparison.Ordinal) ||
            string.Equals(newName, ".", StringComparison.Ordinal) ||
            string.Equals(newName, "..", StringComparison.Ordinal))
            return -(int)Errno.EINVAL;
        if (ReferenceEquals(this, targetParent) && string.Equals(oldName, newName, StringComparison.Ordinal))
            return 0;

        var sourceEntry = Lookup(oldName);
        if (sourceEntry == null)
            return -(int)Errno.ENOENT;
        if (sourceEntry.Inode is not OverlayInode sourceOverlay)
            return -(int)Errno.EXDEV;

        var targetEntry = targetParent.Lookup(newName);
        if (targetEntry != null && ReferenceEquals(targetEntry.Inode, sourceEntry.Inode))
            return 0;

        if (targetEntry != null && targetEntry.Inode?.Type != InodeType.Directory &&
            sourceOverlay.Type == InodeType.Directory)
            return -(int)Errno.ENOTDIR;

        if (targetEntry?.Inode?.Type == InodeType.Directory)
        {
            if (sourceOverlay.Type != InodeType.Directory)
                return -(int)Errno.EISDIR;
            if (targetEntry.Inode.GetEntries().Any(e => e.Name is not "." and not ".."))
                return -(int)Errno.ENOTEMPTY;

            // If target directory is logically empty but exists in upper (e.g. contains whiteouts),
            // we must physically remove it from upper before rename can replace it.
            if (targetParent.UpperInode != null && targetEntry.Inode is OverlayInode targetOverlay &&
                targetOverlay.UpperInode != null)
            {
                // Clear all physical entries (whiteouts) in upper to allow rmdir
                foreach (var e in targetOverlay.UpperInode.GetEntries().Where(e => e.Name is not "." and not "..")
                             .ToList())
                {
                    var unlinkRc = targetOverlay.UpperInode.Unlink(e.Name);
                    if (unlinkRc < 0)
                        return unlinkRc;
                }

                var rmdirTargetRc = targetParent.UpperInode.Rmdir(newName);
                if (rmdirTargetRc < 0)
                    return rmdirTargetRc;
            }
        }

        var targetLowerEntry = targetParent.LookupInAnyLower(newName);
        var targetHasLowerDirectoryBacking = targetLowerEntry?.Inode?.Type == InodeType.Directory;
        var sourceLowerOnly = sourceOverlay.UpperInode == null && sourceOverlay.LowerInode != null;
        var sourceHasLowerBacking = sourceOverlay.LowerInode != null;

        // Rename mutates directory entries, so parents must exist in upper.
        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        if (targetParent.UpperDentry == null)
        {
            var copyTargetRc = targetParent.CopyUpDirectory();
            if (copyTargetRc < 0)
                return copyTargetRc;
        }

        if (sourceLowerOnly)
        {
            var copyRc = sourceOverlay.CopyUpToCurrentParent(oldName, this, null);
            if (copyRc < 0)
                return copyRc;
        }

        if (UpperInode == null || targetParent.UpperInode == null)
            return -(int)Errno.EROFS;

        var osb = (OverlaySuperBlock)SuperBlock;
        // Destination whiteout must be cleared before rename places a new visible entry at newName.
        osb.WhiteoutCodec.ClearEncodedWhiteout(targetParent, newName);
        osb.RemoveWhiteout(new InodeKey(targetParent.Dev, targetParent.Ino), newName);

        var renameRc = UpperInode.Rename(oldName, targetParent.UpperInode, newName);
        if (renameRc < 0)
            return renameRc;

        // Any source that still has lower backing leaves the old lower name behind after upper rename.
        if (sourceHasLowerBacking)
        {
            osb.AddWhiteout(new InodeKey(Dev, Ino), oldName);
            osb.WhiteoutCodec.TryCreateEncodedWhiteout(this, oldName);
        }

        if (sourceOverlay.Type == InodeType.Directory && targetHasLowerDirectoryBacking && !sourceHasLowerBacking)
            osb.MarkOpaque(new InodeKey(sourceOverlay.Dev, sourceOverlay.Ino));

        if (!ReferenceEquals(_state, targetParent._state) || !string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            _state.TryRemoveChildState(oldName, sourceOverlay._state);
            targetParent._state.AttachChildState(newName, sourceOverlay._state, sourceOverlay.LowerDentries,
                sourceOverlay.UpperDentry);
        }

        foreach (var alias in Dentries)
            alias.TryUncacheChild(oldName, "OverlayInode.Rename.old-parent", out _);
        foreach (var alias in targetParent.Dentries)
            alias.TryUncacheChild(newName, "OverlayInode.Rename.new-parent.drop-stale", out _);

        if (targetEntry?.Inode is OverlayInode overwrittenOverlay)
            overwrittenOverlay.RefreshOverlayLinkCountFromSource("OverlayInode.Rename.overwrite-target");
        if (sourceOverlay.Type == InodeType.Directory)
        {
            RefreshOverlayLinkCountFromSource("OverlayInode.Rename.old-parent-refresh");
            if (!ReferenceEquals(this, targetParent))
                targetParent.RefreshOverlayLinkCountFromSource("OverlayInode.Rename.new-parent-refresh");
        }
        else
        {
            sourceOverlay.RefreshOverlayLinkCountFromSource("OverlayInode.Rename.source-refresh");
        }

        return 0;
    }

    public override int Link(Dentry dentry, Inode oldInode)
    {
        if (oldInode is not OverlayInode oldOverlay)
            return -(int)Errno.EXDEV;
        if (Lookup(dentry.Name) != null)
            return -(int)Errno.EEXIST;

        // Link mutates directory entries, so parent must exist in upper
        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        // Source must also be evaluated. If it only exists in lower, it needs to be copied up
        // because we can't create a hardlink in upper pointing to lower.
        // Inoverlayfs, a hardlink to a lower file triggers copy-up of the source.
        if (oldOverlay.UpperInode == null)
        {
            var res = oldOverlay.CopyUp(null);
            if (res < 0)
                return res;
        }

        if (UpperInode == null || oldOverlay.UpperInode == null)
            return -(int)Errno.EROFS;

        var osb = (OverlaySuperBlock)SuperBlock;
        // A hidden lower entry may leave an upper whiteout occupying the target name.
        osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
        osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        var linkRc = UpperInode.Link(upperDentry, oldOverlay.UpperInode);
        if (linkRc < 0)
            return linkRc;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var newOverlayInode = oldOverlay;
        newOverlayInode.InitializeOverlayLinkCount("OverlayInode.Link.copyup-source");
        dentry.Instantiate(newOverlayInode);
        newOverlayInode.RefreshOverlayLinkCountFromSource("OverlayInode.Link.refresh");

        return 0;
    }

    public override int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (string.IsNullOrEmpty(target))
            return -(int)Errno.EINVAL;
        if (Lookup(dentry.Name) != null)
            return -(int)Errno.EEXIST;

        // Symlink mutates directory entries, so parent must exist in upper.
        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        if (UpperDentry == null || UpperInode == null)
            return -(int)Errno.EROFS;

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        var symlinkRc = UpperInode.Symlink(upperDentry, target, uid, gid);
        if (symlinkRc < 0)
            return symlinkRc;
        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        return 0;
    }

    public override int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        if (Lookup(dentry.Name) != null)
            return -(int)Errno.EEXIST;

        // mknod mutates directory entries, so parent must exist in upper.
        if (UpperDentry == null)
        {
            var copyRc = CopyUpDirectory();
            if (copyRc < 0)
                return copyRc;
        }

        if (UpperDentry == null || UpperInode == null)
            return -(int)Errno.EROFS;

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        var mknodRc = UpperInode.Mknod(upperDentry, mode, uid, gid, type, rdev);
        if (mknodRc < 0)
            return mknodRc;

        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        return 0;
    }

    internal void InitializeOverlayLinkCount(string reason)
    {
        if (Type == InodeType.Directory)
        {
            SetInitialLinkCount(ComputeMergedDirectoryLinkCount(), reason);
            return;
        }

        var source = SourceInode;
        var nlink = source != null
            ? checked((int)Math.Min(int.MaxValue, source.GetLinkCountForStat()))
            : 1;
        SetInitialLinkCount(Math.Max(0, nlink), reason);
    }

    private int ComputeMergedDirectoryLinkCount()
    {
        var entries = GetEntries();
        var subdirCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..") continue;
            if (entry.Type == InodeType.Directory) subdirCount++;
        }

        return 2 + subdirCount;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        if (UpperInode == null)
        {
            var res = CopyUp(null);
            if (res < 0) return res;
        }

        return UpperInode!.SetXAttr(name, value, flags);
    }

    public override int GetXAttr(string name, Span<byte> value)
    {
        if (UpperInode != null) return UpperInode.GetXAttr(name, value);
        if (LowerInode != null) return LowerInode.GetXAttr(name, value);
        return -(int)Errno.ENODATA;
    }

    public override int ListXAttr(Span<byte> list)
    {
        if (UpperInode != null) return UpperInode.ListXAttr(list);
        if (LowerInode != null) return LowerInode.ListXAttr(list);
        return 0;
    }

    public override int RemoveXAttr(string name)
    {
        if (UpperInode == null)
        {
            var res = CopyUp(null);
            if (res < 0) return res;
        }

        return UpperInode!.RemoveXAttr(name);
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return ReadWithPageCache(linuxFile, buffer, offset, BackendRead);
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (linuxFile == null) return -(int)Errno.EBADF;
        var copyRc = EnsureWritableBacking(linuxFile);
        if (copyRc < 0)
            return copyRc;

        var source = ResolveSourceForFile(linuxFile);
        if (source == null)
            return -(int)Errno.EROFS;
        return source.WriteFromHost(null, linuxFile, buffer, offset);
    }

    public override int ReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        pageBuffer.Clear();
        if (request.Length == 0) return 0;
        var source = ResolvePagingSource(linuxFile);
        if (source != null)
            return source.ReadPage(linuxFile, request, pageBuffer);

        var rc = BackendRead(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        return rc < 0 ? rc : 0;
    }

    public override int Readahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        var source = ResolvePagingSource(linuxFile);
        return source?.Readahead(linuxFile, request) ?? 0;
    }

    public override int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0) return 0;
        if (linuxFile == null) return -(int)Errno.EBADF;

        var copyRc = EnsureWritableBacking(linuxFile);
        if (copyRc < 0) return copyRc;

        var source = ResolveSourceForFile(linuxFile);
        if (source == null) return -(int)Errno.EROFS;
        return source.WritePage(linuxFile, request, pageBuffer, sync);
    }

    public override int WritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        var source = ResolveSourceForFile(linuxFile);
        if (source != null) return source.WritePages(linuxFile, request);
        return 0;
    }

    public override int SetPageDirty(long pageIndex)
    {
        var source = UpperInode ?? LowerInode ?? GetAnyOpenBackingInode();
        if (source != null) return source.SetPageDirty(pageIndex);
        return 0;
    }

    public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        bool writable, out IPageHandle? pageHandle)
    {
        if (writable)
        {
            var copyRc = EnsureWritableBacking(linuxFile);
            if (copyRc < 0)
            {
                pageHandle = null;
                return false;
            }
        }

        var source = ResolveSourceForFile(linuxFile);
        if (source == null)
        {
            pageHandle = null;
            return false;
        }

        return source.TryAcquireMappedPageHandle(linuxFile, pageIndex, absoluteFileOffset, writable, out pageHandle);
    }

    public override bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex)
    {
        var source = ResolveSourceForFile(linuxFile);
        return source?.TryFlushMappedPage(linuxFile, pageIndex) == true;
    }

    internal override int SyncCachedPage(LinuxFile? linuxFile, AddressSpace mapping,
        PageSyncRequest request)
    {
        var source = ResolveSourceForFile(linuxFile) as MappingBackedInode;
        return source?.SyncCachedPage(linuxFile, mapping, request) ?? 0;
    }

    internal override int SyncCachedPages(LinuxFile? linuxFile, AddressSpace mapping,
        WritePagesRequest request)
    {
        var source = ResolveSourceForFile(linuxFile) as MappingBackedInode;
        return source?.SyncCachedPages(linuxFile, mapping, request) ?? 0;
    }

    public override void Open(LinuxFile linuxFile)
    {
        var source = UpperInode ?? LowerInode;
        if (source == null) return;
        BindFileBacking(linuxFile, source, "OverlayInode.Open");
    }

    public override void Release(LinuxFile linuxFile)
    {
        _ = Flock(linuxFile, LinuxConstants.LOCK_UN);
        UnbindFileBacking(linuxFile, "OverlayInode.Release");
    }

    protected override void OnEvictCache()
    {
        _state.UnregisterAlias(this);
        base.OnEvictCache();
    }

    protected override void OnFinalizeDelete()
    {
        _state.UnregisterAlias(this);
        base.OnFinalizeDelete();
    }

    public override int Truncate(long size)
    {
        if (UpperInode != null) return UpperInode.Truncate(size);
        if (LowerInode == null)
        {
            var detachedBacking = GetAnyOpenBackingInode();
            return detachedBacking?.Truncate(size) ?? -(int)Errno.EROFS;
        }

        var res = CopyUp(null);
        if (res < 0) return res;
        return UpperInode!.Truncate(size);
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var osb = (OverlaySuperBlock)SuperBlock;
        var dirKey = new InodeKey(Dev, Ino);
        var entries = new Dictionary<string, DirectoryEntry>();
        var dirOpaque = osb.IsOpaque(dirKey) || osb.WhiteoutCodec.IsEncodedOpaque(UpperInode);

        // Merge lower layers from bottom to top so higher lower layers override.
        if (!dirOpaque)
            for (var i = LowerDentries.Count - 1; i >= 0; i--)
            {
                var inode = LowerDentries[i].Inode;
                if (inode == null) continue;
                foreach (var e in inode.GetEntries())
                    entries[e.Name] = e;
            }

        if (UpperInode != null)
            foreach (var e in UpperInode.GetEntries())
            {
                if (e.Name is "." or "..")
                {
                    entries[e.Name] = e;
                    continue;
                }

                var child = UpperInode.Lookup(e.Name);
                if (osb.WhiteoutCodec.IsEncodedOpaqueEntry(e))
                {
                    osb.MarkOpaque(dirKey);
                    continue;
                }

                if (osb.WhiteoutCodec.TryDecodeEncodedWhiteout(e, child?.Inode, out var targetName))
                {
                    osb.AddWhiteout(dirKey, targetName);
                    entries.Remove(targetName);
                    continue;
                }

                entries[e.Name] = e;
            }

        foreach (var whiteout in osb.GetWhiteouts(dirKey))
            entries.Remove(whiteout);

        return [.. entries.Values];
    }

    private Dentry? LookupInAnyLower(string name)
    {
        foreach (var lower in LowerDentries)
        {
            var candidate = lower.Inode?.Lookup(name);
            if (candidate != null) return candidate;
        }

        return null;
    }

    private int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        var source = ResolveSourceForFile(linuxFile);
        if (source != null) return source.ReadToHost(null, linuxFile!, buffer, offset);
        return 0;
    }

    private int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (linuxFile == null) return -(int)Errno.EBADF;
        var copyRc = EnsureWritableBacking(linuxFile);
        if (copyRc < 0) return copyRc;

        var source = ResolveSourceForFile(linuxFile);
        if (source == null) return -(int)Errno.EROFS;
        return source.WriteFromHost(null, linuxFile, buffer, offset);
    }
}

public interface IOverlayWhiteoutCodec
{
    bool IsInternalMarkerName(string name);
    bool HasEncodedWhiteout(Inode? upperDir, string name);
    bool IsEncodedOpaque(Inode? upperDir);
    bool IsEncodedOpaqueEntry(DirectoryEntry entry);
    bool TryDecodeEncodedWhiteout(DirectoryEntry entry, Inode? inode, out string targetName);
    bool IsWhiteoutInode(Inode? inode);
    bool TryCreateEncodedWhiteout(OverlayInode dir, string name);
    void ClearEncodedWhiteout(OverlayInode dir, string name);
}

public sealed class LogicalWhiteoutCodec : IOverlayWhiteoutCodec
{
    public bool IsInternalMarkerName(string name)
    {
        return false;
    }

    public bool HasEncodedWhiteout(Inode? upperDir, string name)
    {
        return false;
    }

    public bool IsEncodedOpaque(Inode? upperDir)
    {
        return false;
    }

    public bool IsEncodedOpaqueEntry(DirectoryEntry entry)
    {
        return false;
    }

    public bool TryDecodeEncodedWhiteout(DirectoryEntry entry, Inode? inode, out string targetName)
    {
        targetName = string.Empty;
        return false;
    }

    public bool IsWhiteoutInode(Inode? inode)
    {
        return false;
    }

    public bool TryCreateEncodedWhiteout(OverlayInode dir, string name)
    {
        return false;
    }

    public void ClearEncodedWhiteout(OverlayInode dir, string name)
    {
    }
}

public sealed class HybridWhiteoutCodec : IOverlayWhiteoutCodec
{
    private const string OpaqueMarker = ".wh..wh..opq";

    public bool IsInternalMarkerName(string name)
    {
        return name == OpaqueMarker || name.StartsWith(".wh.", StringComparison.Ordinal);
    }

    public bool HasEncodedWhiteout(Inode? upperDir, string name)
    {
        if (upperDir == null) return false;
        if (upperDir.Lookup($".wh.{name}") != null) return true;
        var sameName = upperDir.Lookup(name);
        return IsWhiteoutInode(sameName?.Inode);
    }

    public bool IsEncodedOpaque(Inode? upperDir)
    {
        if (upperDir == null) return false;
        return upperDir.Lookup(OpaqueMarker) != null;
    }

    public bool IsEncodedOpaqueEntry(DirectoryEntry entry)
    {
        return entry.Name == OpaqueMarker;
    }

    public bool TryDecodeEncodedWhiteout(DirectoryEntry entry, Inode? inode, out string targetName)
    {
        if (entry.Name.StartsWith(".wh.", StringComparison.Ordinal) && entry.Name != OpaqueMarker)
        {
            targetName = entry.Name[4..];
            return !string.IsNullOrEmpty(targetName);
        }

        if (IsWhiteoutInode(inode))
        {
            targetName = entry.Name;
            return true;
        }

        targetName = string.Empty;
        return false;
    }

    public bool IsWhiteoutInode(Inode? inode)
    {
        return inode != null && inode.Type == InodeType.CharDev && inode.Rdev == 0;
    }

    public bool TryCreateEncodedWhiteout(OverlayInode dir, string name)
    {
        if (dir.UpperInode == null || dir.UpperDentry == null) return false;
        if (dir.UpperInode is not TmpfsInode && dir.UpperInode is not HostInode && dir.UpperInode is not SilkInode)
            return false;
        if (dir.UpperDentry == null) return false;
        if (dir.UpperInode.Lookup(name) != null) return false;

        var encodedName = dir.UpperInode is SilkInode ? $".wh.{name}" : name;
        if (dir.UpperInode.Lookup(encodedName) != null) return false;

        var dentry = new Dentry(encodedName, null, dir.UpperDentry, dir.UpperDentry.SuperBlock);
        dir.UpperInode.Mknod(dentry, 0x1B6, 0, 0, InodeType.CharDev, 0); // char 0/0

        return true;
    }

    public void ClearEncodedWhiteout(OverlayInode dir, string name)
    {
        if (dir.UpperInode == null) return;
        var existing = dir.UpperInode.Lookup(name);
        if (existing?.Inode != null && IsWhiteoutInode(existing.Inode))
        {
            dir.UpperInode.Unlink(name);
            return;
        }

        var encoded = dir.UpperInode.Lookup($".wh.{name}");
        if (encoded != null)
            dir.UpperInode.Unlink($".wh.{name}");
    }
}