namespace Fiberish.Auth.Cred;

public sealed class GroupInfo
{
    public List<int> Groups { get; }

    public GroupInfo(IEnumerable<int>? groups = null)
    {
        Groups = groups?.Distinct().ToList() ?? [];
    }

    public bool Contains(int gid)
    {
        return Groups.Contains(gid);
    }
}

public sealed class Cred
{
    public int Ruid { get; set; }
    public int Euid { get; set; }
    public int Suid { get; set; }
    public int Fsuid { get; set; }

    public int Rgid { get; set; }
    public int Egid { get; set; }
    public int Sgid { get; set; }
    public int Fsgid { get; set; }

    public GroupInfo Groups { get; set; } = new();
    public int Umask { get; set; }

    public Cred Clone()
    {
        return new Cred
        {
            Ruid = Ruid,
            Euid = Euid,
            Suid = Suid,
            Fsuid = Fsuid,
            Rgid = Rgid,
            Egid = Egid,
            Sgid = Sgid,
            Fsgid = Fsgid,
            Groups = new GroupInfo(Groups.Groups),
            Umask = Umask
        };
    }
}
