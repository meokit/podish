using System.Runtime.CompilerServices;
using Fiberish.Core;

namespace Fiberish.VFS;

/// <summary>
///     Represents a mount namespace containing all mounts and their lookup hash.
///     This class encapsulates both the list of mounts and the hash table for efficient lookup,
///     making it easy to share or clone the entire mount namespace.
/// </summary>
public class MountNamespace
{
    /// <summary>
    ///     Hash-based lookup for child mounts: (parent_mount, mount_point_dentry) -> child_mount.
    ///     Equivalent to Linux's mount hash table.
    /// </summary>
    private readonly Dictionary<(Mount?, Dentry), Mount> _mountHash = [];

    /// <summary>
    ///     List of all Mount objects in this namespace.
    /// </summary>
    private readonly List<Mount> _mounts = [];

    private int _refCount = 1;
    private Mount? _rootMount;

    /// <summary>
    ///     The root mount (for the filesystem namespace).
    /// </summary>
    public Mount? RootMount
    {
        get
        {
            using (EnterNamespaceScope())
            {
                return _rootMount;
            }
        }
        set
        {
            using (EnterNamespaceScope())
            {
                _rootMount = value;
            }
        }
    }

    /// <summary>
    ///     Gets read-only view of all mounts in this namespace.
    /// </summary>
    public IReadOnlyList<Mount> Mounts
    {
        get
        {
            using (EnterNamespaceScope())
            {
                return _mounts;
            }
        }
    }

    public int RefCount => _refCount;

    /// <summary>
    ///     Gets the number of mounts in this namespace.
    /// </summary>
    public int Count
    {
        get
        {
            using (EnterNamespaceScope())
            {
                return _mounts.Count;
            }
        }
    }

    private static NamespaceScope EnterNamespaceScope([CallerMemberName] string? caller = null)
    {
        KernelScheduler.Current?.AssertSchedulerThread(caller);
        return default;
    }

    /// <summary>
    ///     Registers a mount in the namespace and attaches it to a mount point.
    /// </summary>
    public void RegisterMount(Mount mount, Mount? parent, Dentry mountPoint)
    {
        using (EnterNamespaceScope())
        {
            if (!mount.IsAttached)
                mount.Attach(mountPoint, parent);

            // Mount namespace owns one reference while attached.
            mount.Get();

            _mounts.Add(mount);
            _mountHash[(parent, mountPoint)] = mount;
        }
    }

    /// <summary>
    ///     Unregisters a mount from the namespace and detaches it.
    /// </summary>
    public void UnregisterMount(Mount mount)
    {
        using (EnterNamespaceScope())
        {
            var parent = mount.Parent;
            var mountPoint = mount.MountPoint;

            mount.Detach();
            _mounts.Remove(mount);

            if (mountPoint != null)
                _mountHash.Remove((parent, mountPoint));

            // Drop mount namespace ownership.
            mount.Put();
        }
    }

    /// <summary>
    ///     Find a child mount at the given dentry in the parent mount.
    /// </summary>
    public Mount? FindMount(Mount? parent, Dentry dentry)
    {
        using (EnterNamespaceScope())
        {
            return _mountHash.GetValueOrDefault((parent, dentry));
        }
    }

    /// <summary>
    ///     Checks if a mount exists at the given location.
    /// </summary>
    public bool HasMountAt(Mount? parent, Dentry dentry)
    {
        using (EnterNamespaceScope())
        {
            return _mountHash.ContainsKey((parent, dentry));
        }
    }

    /// <summary>
    ///     Creates a shallow copy of this namespace for sharing between processes.
    ///     The mounts themselves are shared (not cloned).
    /// </summary>
    public MountNamespace Share()
    {
        Get();
        return this;
    }

    /// <summary>
    ///     Gets an enumerator for iterating over all mounts.
    /// </summary>
    public List<Mount>.Enumerator GetEnumerator()
    {
        using (EnterNamespaceScope())
        {
            return _mounts.ToList().GetEnumerator();
        }
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
        List<Mount> mountsSnapshot;
        using (EnterNamespaceScope())
        {
            mountsSnapshot = _mounts.ToList();
        }

        foreach (var mount in mountsSnapshot)
        {
            var target = BuildMountPath(mount);
            yield return (mount.Source ?? "none", target, mount.FsType ?? "unknown", mount.Options ?? "rw");
        }
    }

    public IEnumerable<MountInfoEntry> GetMountInfoEntries()
    {
        List<Mount> mountsSnapshot;
        using (EnterNamespaceScope())
        {
            mountsSnapshot = _mounts.ToList();
        }

        foreach (var mount in mountsSnapshot)
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

    public void Get()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Put()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
            return;

        List<Mount> mountsToRelease;
        using (EnterNamespaceScope())
        {
            mountsToRelease = _mounts.ToList();
        }

        foreach (var mount in mountsToRelease)
            UnregisterMount(mount);

        using (EnterNamespaceScope())
        {
            _rootMount = null;
        }
    }

    public readonly record struct MountInfoEntry(
        long Id,
        long ParentId,
        string Source,
        string Target,
        string FsType,
        string Options);

    private readonly struct NamespaceScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}