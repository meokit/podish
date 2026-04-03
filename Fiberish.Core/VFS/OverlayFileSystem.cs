using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

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
}

public class OverlayInode : Inode
{
    private readonly OverlayNodeState _state;
    private readonly Dictionary<LinuxFile, Inode> _openBackingByFile = [];

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
        return LowerInode ?? GetAnyOpenBackingInode();
    }

    private Inode? ResolvePagingSource(LinuxFile? linuxFile)
    {
        return ResolveSourceForFile(linuxFile);
    }

    internal Inode? ResolveMmapSource(LinuxFile? linuxFile)
    {
        return ResolvePagingSource(linuxFile);
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
        backing.Open(linuxFile);
        _openBackingByFile[linuxFile] = backing;
    }

    private void UnbindFileBacking(LinuxFile linuxFile, string reason)
    {
        if (!_openBackingByFile.Remove(linuxFile, out var backing))
            return;

        backing.Release(linuxFile);
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

    public int CopyUp(LinuxFile? linuxFile)
    {
        lock (_state.SyncRoot)
        {
            if (UpperInode != null) return 0;
            if (LowerDentry == null) throw new InvalidOperationException("No lower dentry to copy up");

            var upperParent = EnsureParentUpper(LowerDentry);
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
                currentParent.CopyUpDirectory();
            if (currentParent.UpperDentry == null)
                throw new InvalidOperationException("Current upper parent is unavailable for copy-up");

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

            upperParent.Inode!.Mkdir(upperDentry, Mode, Uid, Gid);
            _state.SetUpperDentry(upperDentry);
            PromoteStateBackings(UpperInode, linuxFile);
            return 0;
        }

        var lowerInode = LowerInode;
        try
        {
            switch (Type)
            {
                case InodeType.Symlink:
                    if (lowerInode == null)
                        throw new InvalidOperationException("No lower inode available for symlink copy-up");
                    upperParent.Inode!.Symlink(upperDentry, lowerInode.Readlink(), Uid, Gid);
                    break;

                case InodeType.CharDev:
                case InodeType.BlockDev:
                case InodeType.Fifo:
                case InodeType.Socket:
                    upperParent.Inode!.Mknod(upperDentry, Mode, Uid, Gid, Type, Rdev);
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

                    upperParent.Inode!.Create(upperDentry, Mode, Uid, Gid);

                    if (lowerInode != null)
                    {
                        var buf = new byte[4096];
                        long pos = 0;
                        while (true)
                        {
                            var n = lowerInode.ReadToHost(null, null!, buf, pos);
                            if (n < 0)
                                throw new IOException($"copy-up read failed rc={n}");
                            if (n == 0) break;
                            var writeRc = upperDentry.Inode!.WriteFromHost(null, null!, buf.AsSpan(0, n), pos);
                            if (writeRc != n)
                                throw new IOException($"copy-up write failed rc={writeRc} expected={n}");
                            pos += n;
                        }
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                upperParent.Inode!.Unlink(upperName);
            }
            catch
            {
            }

            Logging.CreateLogger<OverlayInode>()
                .LogWarning("CopyUp failed for {Name}: {Error}", upperName, ex.Message);
            return -(int)Errno.EACCES;
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
        upperParentOfParent.Inode!.Mkdir(newUpperParent, parentLower.Inode!.Mode, parentLower.Inode.Uid,
            parentLower.Inode.Gid);
        return newUpperParent;
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

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Lookup(dentry.Name) != null)
            throw new InvalidOperationException("Exists");

        // Create in Upper.
        if (UpperDentry == null)
            CopyUpDirectory();
        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        // Delegate to Upper
        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode!.Create(upperDentry, mode, uid, gid);

        // Now update the overlay dentry's inode
        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState); // Created only in upper
        dentry.Instantiate(newOverlayInode);

        return dentry;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (Lookup(dentry.Name) != null)
            throw new InvalidOperationException("Exists");

        if (UpperDentry == null)
            CopyUpDirectory();
        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode!.Mkdir(upperDentry, mode, uid, gid);

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        NamespaceOps.OnDirectoryCreated(this, newOverlayInode, "OverlayInode.Mkdir");

        return dentry;
    }

    /// <summary>
    ///     Copy-up a lower-only directory to the upper FS.
    ///     Creates an empty directory in the upper FS with the same mode/uid/gid.
    ///     Does NOT copy children — they remain in the lower layer and are merged via Lookup.
    /// </summary>
    private void CopyUpDirectory()
    {
        if (UpperDentry != null) return;
        if (LowerDentry == null)
            throw new InvalidOperationException("Cannot copy-up: no lower dentry");

        _state.SetUpperDentry(EnsureUpperDir(LowerDentry));
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
        if (LowerInode != null)
        {
            var rc = LowerInode.Flock(linuxFile, operation);
            if (rc != -(int)Errno.ENOSYS) return rc;
            return ((OverlaySuperBlock)SuperBlock).Flock(new InodeKey(Dev, Ino), linuxFile, operation);
        }

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
        var overlayEntry = Lookup(name);
        if (overlayEntry == null)
            throw new FileNotFoundException("Source does not exist", name);
        if (overlayEntry.Inode?.Type == InodeType.Directory)
            throw new InvalidOperationException("Is a directory");

        var inUpper = UpperInode?.Lookup(name) != null;
        var inLower = LookupInAnyLower(name) != null;
        var osb = (OverlaySuperBlock)SuperBlock;

        if (inUpper) UpperInode!.Unlink(name);

        if (inLower)
        {
            if (UpperDentry == null) CopyUpDirectory();
            osb.AddWhiteout(new InodeKey(Dev, Ino), name);
            osb.WhiteoutCodec.TryCreateEncodedWhiteout(this, name);
        }

        NamespaceOps.OnEntryRemoved(overlayEntry.Inode, "OverlayInode.Unlink");
    }

    public override void Rmdir(string name)
    {
        var overlayEntry = Lookup(name);
        if (overlayEntry == null)
            throw new DirectoryNotFoundException(name);
        if (overlayEntry.Inode?.Type != InodeType.Directory)
            throw new InvalidOperationException("Not a directory");
        if (overlayEntry.Inode.GetEntries().Any(e => e.Name is not "." and not ".."))
            throw new InvalidOperationException("Directory not empty");

        var inUpper = UpperInode?.Lookup(name) != null;
        var inLower = LookupInAnyLower(name) != null;
        var osb = (OverlaySuperBlock)SuperBlock;
        if (inUpper)
        {
            if (UpperInode!.Lookup(name) is { Inode: { } upperDirInode } upperDir)
            {
                RemoveUpperOverlayInternalEntries(upperDir);
                if (upperDirInode.GetEntries().Any(e => e.Name is not "." and not ".."))
                    throw new InvalidOperationException("Directory not empty");
            }

            UpperInode.Rmdir(name);
        }
        if (inLower)
        {
            if (UpperDentry == null) CopyUpDirectory();
            if (UpperInode?.Lookup(name) is { } upperDir &&
                upperDir.Inode?.GetEntries().Any(e => e.Name is not "." and not "..") == true)
                throw new InvalidOperationException("Directory not empty");
            osb.AddWhiteout(new InodeKey(Dev, Ino), name);
            osb.WhiteoutCodec.TryCreateEncodedWhiteout(this, name);
        }

        NamespaceOps.OnDirectoryRemoved(this, overlayEntry.Inode!, "OverlayInode.Rmdir");
    }

    private void RemoveUpperOverlayInternalEntries(Dentry upperDir)
    {
        var upperDirInode = upperDir.Inode;
        if (upperDirInode == null) return;

        var osb = (OverlaySuperBlock)SuperBlock;
        foreach (var entry in upperDirInode.GetEntries().Where(e => e.Name is not "." and not "..").ToList())
        {
            var child = upperDirInode.Lookup(entry.Name);
            if (!osb.WhiteoutCodec.IsEncodedOpaqueEntry(entry) &&
                !osb.WhiteoutCodec.TryDecodeEncodedWhiteout(entry, child?.Inode, out _))
                continue;

            upperDirInode.Unlink(entry.Name);
        }
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (newParent is not OverlayInode targetParent)
            throw new InvalidOperationException("Target parent is not overlay inode");
        if (string.Equals(oldName, ".", StringComparison.Ordinal) ||
            string.Equals(oldName, "..", StringComparison.Ordinal) ||
            string.Equals(newName, ".", StringComparison.Ordinal) ||
            string.Equals(newName, "..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid rename path");
        if (ReferenceEquals(this, targetParent) && string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        var sourceEntry = Lookup(oldName) ?? throw new FileNotFoundException("Source not found", oldName);
        if (sourceEntry.Inode is not OverlayInode sourceOverlay)
            throw new InvalidOperationException("Source is not overlay inode");

        var targetEntry = targetParent.Lookup(newName);
        if (targetEntry != null && ReferenceEquals(targetEntry.Inode, sourceEntry.Inode))
            return;

        if (targetEntry != null && targetEntry.Inode?.Type != InodeType.Directory &&
            sourceOverlay.Type == InodeType.Directory)
            throw new InvalidOperationException("Not a directory");

        if (targetEntry?.Inode?.Type == InodeType.Directory)
        {
            if (sourceOverlay.Type != InodeType.Directory)
                throw new InvalidOperationException("Is a directory");
            if (targetEntry.Inode.GetEntries().Any(e => e.Name is not "." and not ".."))
                throw new InvalidOperationException("Directory not empty");

            // If target directory is logically empty but exists in upper (e.g. contains whiteouts),
            // we must physically remove it from upper before rename can replace it.
            if (targetParent.UpperInode != null && targetEntry.Inode is OverlayInode targetOverlay && targetOverlay.UpperInode != null)
            {
                // Clear all physical entries (whiteouts) in upper to allow rmdir
                foreach (var e in targetOverlay.UpperInode.GetEntries().Where(e => e.Name is not "." and not "..").ToList())
                    targetOverlay.UpperInode.Unlink(e.Name);

                targetParent.UpperInode.Rmdir(newName);
            }
        }

        var targetLowerEntry = targetParent.LookupInAnyLower(newName);
        var targetHasLowerDirectoryBacking = targetLowerEntry?.Inode?.Type == InodeType.Directory;
        var sourceLowerOnly = sourceOverlay.UpperInode == null && sourceOverlay.LowerInode != null;
        var sourceHasLowerBacking = sourceOverlay.LowerInode != null;

        // Rename mutates directory entries, so parents must exist in upper.
        if (UpperDentry == null)
            CopyUpDirectory();
        if (targetParent.UpperDentry == null)
            targetParent.CopyUpDirectory();

        if (sourceLowerOnly)
        {
            var copyRc = sourceOverlay.CopyUpToCurrentParent(oldName, this, null);
            if (copyRc < 0)
                throw new IOException($"CopyUp failed during Rename with error {copyRc}");
        }

        if (UpperInode == null || targetParent.UpperInode == null)
            throw new InvalidOperationException("Upper directory is unavailable for rename");

        var osb = (OverlaySuperBlock)SuperBlock;
        // Destination whiteout must be cleared before rename places a new visible entry at newName.
        osb.WhiteoutCodec.ClearEncodedWhiteout(targetParent, newName);
        osb.RemoveWhiteout(new InodeKey(targetParent.Dev, targetParent.Ino), newName);

        UpperInode.Rename(oldName, targetParent.UpperInode, newName);

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

        if (targetEntry?.Inode != null)
            NamespaceOps.OnRenameOverwrite(sourceEntry.Inode, targetEntry.Inode,
                "OverlayInode.Rename.overwrite-target");
        if (sourceOverlay.Type == InodeType.Directory && !ReferenceEquals(this, targetParent))
            NamespaceOps.OnDirectoryMovedAcrossParents(this, targetParent, "OverlayInode.Rename");
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        if (oldInode is not OverlayInode oldOverlay)
            throw new InvalidOperationException("Source is not an overlay inode");
        if (Lookup(dentry.Name) != null)
            throw new InvalidOperationException("Exists");

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

        var osb = (OverlaySuperBlock)SuperBlock;
        // A hidden lower entry may leave an upper whiteout occupying the target name.
        osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
        osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode.Link(upperDentry, oldOverlay.UpperInode);
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var newOverlayInode = oldOverlay;
        newOverlayInode.InitializeOverlayLinkCount("OverlayInode.Link.copyup-source");
        dentry.Instantiate(newOverlayInode);
        NamespaceOps.OnLinkAdded(oldOverlay, "OverlayInode.Link");

        return dentry;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (string.IsNullOrEmpty(target))
            throw new ArgumentException("Symlink target cannot be null or empty", nameof(target));
        if (Lookup(dentry.Name) != null)
            throw new InvalidOperationException("Exists");

        // Symlink mutates directory entries, so parent must exist in upper.
        if (UpperDentry == null)
            CopyUpDirectory();

        if (UpperDentry == null || UpperInode == null)
            throw new InvalidOperationException("Upper directory is unavailable for symlink");

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode.Symlink(upperDentry, target, uid, gid);
        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        return dentry;
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        if (Lookup(dentry.Name) != null)
            throw new InvalidOperationException("Exists");

        // mknod mutates directory entries, so parent must exist in upper.
        if (UpperDentry == null)
            CopyUpDirectory();

        if (UpperDentry == null || UpperInode == null)
            throw new InvalidOperationException("Upper directory is unavailable for mknod");

        var upperDentry = new Dentry(dentry.Name, null, UpperDentry, ((OverlaySuperBlock)SuperBlock).UpperSB);
        UpperInode.Mknod(upperDentry, mode, uid, gid, type, rdev);

        var osb = (OverlaySuperBlock)SuperBlock;
        if (!osb.WhiteoutCodec.IsInternalMarkerName(dentry.Name))
        {
            osb.WhiteoutCodec.ClearEncodedWhiteout(this, dentry.Name);
            osb.RemoveWhiteout(new InodeKey(Dev, Ino), dentry.Name);
        }

        var childState = _state.GetOrCreateChildState(dentry.Name, null, upperDentry);
        var newOverlayInode = new OverlayInode(SuperBlock, null, upperDentry, childState);
        dentry.Instantiate(newOverlayInode);
        return dentry;
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
        return WriteWithPageCache(linuxFile, buffer, offset, BackendWrite);
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

        if (UpperInode == null && LowerInode != null)
        {
            var res = CopyUp(linuxFile);
            if (res < 0) return res;
        }

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
        if (UpperInode == null && LowerInode != null)
        {
            var res = CopyUp(linuxFile);
            if (res < 0) return res;
        }

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
