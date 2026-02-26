namespace Fiberish.VFS;

/// <summary>
///     Represents a mount namespace containing all mounts and their lookup hash.
///     This class encapsulates both the list of mounts and the hash table for efficient lookup,
///     making it easy to share or clone the entire mount namespace.
/// </summary>
public class MountNamespace
{
    public readonly record struct MountInfoEntry(
        long Id,
        long ParentId,
        string Source,
        string Target,
        string FsType,
        string Options);

    /// <summary>
    ///     List of all Mount objects in this namespace.
    /// </summary>
    private readonly List<Mount> _mounts = [];

    /// <summary>
    ///     Hash-based lookup for child mounts: (parent_mount, mount_point_dentry) -> child_mount.
    ///     Equivalent to Linux's mount hash table.
    /// </summary>
    private readonly Dictionary<(Mount?, Dentry), Mount> _mountHash = [];

    /// <summary>
    ///     The root mount (for the filesystem namespace).
    /// </summary>
    public Mount? RootMount { get; set; }

    /// <summary>
    ///     Gets read-only view of all mounts in this namespace.
    /// </summary>
    public IReadOnlyList<Mount> Mounts => _mounts;

    /// <summary>
    ///     Gets the number of mounts in this namespace.
    /// </summary>
    public int Count => _mounts.Count;

    /// <summary>
    ///     Registers a mount in the namespace and attaches it to a mount point.
    /// </summary>
    public void RegisterMount(Mount mount, Mount? parent, Dentry mountPoint)
    {
        if (!mount.IsAttached)
            mount.Attach(mountPoint, parent);

        _mounts.Add(mount);
        _mountHash[(parent, mountPoint)] = mount;
    }

    /// <summary>
    ///     Unregisters a mount from the namespace and detaches it.
    /// </summary>
    public void UnregisterMount(Mount mount)
    {
        var parent = mount.Parent;
        var mountPoint = mount.MountPoint;

        mount.Detach();
        _mounts.Remove(mount);

        if (mountPoint != null)
        {
            _mountHash.Remove((parent, mountPoint));
        }
    }

    /// <summary>
    ///     Find a child mount at the given dentry in the parent mount.
    /// </summary>
    public Mount? FindMount(Mount? parent, Dentry dentry)
    {
        return _mountHash.GetValueOrDefault((parent, dentry));
    }

    /// <summary>
    ///     Checks if a mount exists at the given location.
    /// </summary>
    public bool HasMountAt(Mount? parent, Dentry dentry)
    {
        return _mountHash.ContainsKey((parent, dentry));
    }

    /// <summary>
    ///     Creates a shallow copy of this namespace for sharing between processes.
    ///     The mounts themselves are shared (not cloned).
    /// </summary>
    public MountNamespace Share()
    {
        var shared = new MountNamespace
        {
            RootMount = RootMount
        };

        // Share the same mount list and hash
        foreach (var mount in _mounts)
        {
            shared._mounts.Add(mount);
        }

        foreach (var kv in _mountHash)
        {
            shared._mountHash[kv.Key] = kv.Value;
        }

        return shared;
    }

    /// <summary>
    ///     Gets an enumerator for iterating over all mounts.
    /// </summary>
    public List<Mount>.Enumerator GetEnumerator()
    {
        return _mounts.GetEnumerator();
    }

    /// <summary>
    ///     Builds the absolute path for a mount by traversing the mount hierarchy.
    /// </summary>
    private static string BuildMountPath(Mount mount)
    {
        var parts = new List<string>();
        var current = mount;

        while (current != null)
        {
            var mountPoint = current.MountPoint;
            if (mountPoint != null)
            {
                if (mountPoint.Name != "/")
                    parts.Add(mountPoint.Name);
                current = current.Parent;
            }
            else
            {
                // Root mount
                parts.Add("");
                break;
            }
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    /// <summary>
    ///     Generates mount info list for /proc/mounts.
    ///     This is dynamically generated from the current mount state.
    /// </summary>
    public IEnumerable<(string Source, string Target, string FsType, string Options)> GetMountInfos()
    {
        foreach (var mount in _mounts)
        {
            var target = BuildMountPath(mount);
            yield return (mount.Source ?? "none", target, mount.FsType ?? "unknown", mount.Options ?? "rw");
        }
    }

    public IEnumerable<MountInfoEntry> GetMountInfoEntries()
    {
        foreach (var mount in _mounts)
        {
            var target = BuildMountPath(mount);
            var parentId = mount.Parent?.Id ?? 0;
            yield return new MountInfoEntry(
                mount.Id,
                parentId,
                mount.Source ?? "none",
                target,
                mount.FsType ?? "unknown",
                mount.Options ?? "rw");
        }
    }
}
