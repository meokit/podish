using System.Text;
using System.Text.Json;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Podish.Core;

internal sealed class ContainerConfigurationException : Exception
{
    public ContainerConfigurationException(string message) : base(message)
    {
    }
}

internal readonly record struct RequestedUserSpec(string UserToken, string? GroupToken, bool UserIsNumeric,
    bool GroupIsNumeric);

internal sealed record ResolvedCredentials(
    int Uid,
    int Gid,
    IReadOnlyList<int> SupplementaryGroups,
    string UserName,
    string HomeDirectory)
{
    public static ResolvedCredentials Root { get; } = new(0, 0, Array.Empty<int>(), "root", "/root");
}

internal static class ContainerUserResolver
{
    private const string PasswdPath = "/etc/passwd";
    private const string GroupPath = "/etc/group";

    public static string? TryReadImageConfigUser(string ociStoreDir)
    {
        if (string.IsNullOrWhiteSpace(ociStoreDir))
            return null;

        var imagePath = Path.Combine(ociStoreDir, "image.json");
        if (!File.Exists(imagePath))
            return null;

        try
        {
            var image = JsonSerializer.Deserialize(File.ReadAllText(imagePath), PodishJsonContext.Default.OciStoredImage);
            return string.IsNullOrWhiteSpace(image?.ConfigUser) ? null : image.ConfigUser;
        }
        catch
        {
            return null;
        }
    }

    public static ResolvedCredentials Resolve(SyscallManager syscalls, string? rawUser)
    {
        if (string.IsNullOrWhiteSpace(rawUser))
            return ResolvedCredentials.Root;

        var requested = ParseRequestedUser(rawUser);
        var needsPasswd = !requested.UserIsNumeric;
        var needsGroup = !requested.UserIsNumeric || (requested.GroupToken != null && !requested.GroupIsNumeric);

        var passwdEntries = needsPasswd
            ? ReadPasswdEntries(syscalls)
                ?? throw new ContainerConfigurationException(
                    $"container user '{rawUser}' requires guest '{PasswdPath}', but it is missing.")
            : null;
        var groupEntries = needsGroup
            ? ReadGroupEntries(syscalls)
                ?? throw new ContainerConfigurationException(
                    $"container user '{rawUser}' requires guest '{GroupPath}', but it is missing.")
            : null;

        UnixPasswdEntry? passwdEntry = null;
        if (!requested.UserIsNumeric)
        {
            passwdEntry = passwdEntries!.FirstOrDefault(entry => entry.UserName == requested.UserToken);
            if (passwdEntry == null)
                throw new ContainerConfigurationException(
                    $"guest user '{requested.UserToken}' was not found in '{PasswdPath}'.");
        }

        var uid = requested.UserIsNumeric ? ParseNumericId(requested.UserToken, "uid") : passwdEntry!.Uid;
        var gid = ResolvePrimaryGid(requested, rawUser!, passwdEntry, groupEntries, uid);
        var userName = requested.UserIsNumeric ? uid.ToString() : passwdEntry!.UserName;
        var homeDirectory = requested.UserIsNumeric ? "/" : NormalizeHome(passwdEntry!.HomeDirectory);

        List<int> supplementaryGroups = [];
        if (!requested.UserIsNumeric)
        {
            foreach (var entry in groupEntries!)
                if (entry.Gid != gid && entry.Members.Contains(userName, StringComparer.Ordinal))
                    supplementaryGroups.Add(entry.Gid);
        }

        return new ResolvedCredentials(uid, gid, supplementaryGroups.Distinct().ToArray(), userName, homeDirectory);
    }

    public static RequestedUserSpec ParseRequestedUser(string rawUser)
    {
        if (string.IsNullOrWhiteSpace(rawUser))
            throw new ContainerConfigurationException("container user must not be empty.");

        var trimmed = rawUser.Trim();
        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0 && colonIndex != trimmed.LastIndexOf(':'))
            throw new ContainerConfigurationException($"invalid container user specification '{rawUser}'.");

        string userToken;
        string? groupToken = null;
        if (colonIndex >= 0)
        {
            userToken = trimmed[..colonIndex].Trim();
            groupToken = trimmed[(colonIndex + 1)..].Trim();
            if (string.IsNullOrEmpty(userToken) || string.IsNullOrEmpty(groupToken))
                throw new ContainerConfigurationException($"invalid container user specification '{rawUser}'.");
        }
        else
        {
            userToken = trimmed;
        }

        return new RequestedUserSpec(userToken, groupToken, IsNumericId(userToken), groupToken != null && IsNumericId(groupToken));
    }

    private static int ResolvePrimaryGid(RequestedUserSpec requested, string rawUser, UnixPasswdEntry? passwdEntry,
        IReadOnlyList<UnixGroupEntry>? groupEntries, int uid)
    {
        if (requested.GroupToken == null)
            return requested.UserIsNumeric ? uid : passwdEntry!.PrimaryGid;

        if (requested.GroupIsNumeric)
            return ParseNumericId(requested.GroupToken, "gid");

        var groupEntry = groupEntries!.FirstOrDefault(entry => entry.GroupName == requested.GroupToken);
        if (groupEntry == null)
            throw new ContainerConfigurationException(
                $"guest group '{requested.GroupToken}' was not found while resolving '{rawUser}'.");

        return groupEntry.Gid;
    }

    private static List<UnixPasswdEntry>? ReadPasswdEntries(SyscallManager syscalls)
    {
        var bytes = ReadGuestFileBytes(syscalls, PasswdPath);
        if (bytes == null)
            return null;

        var text = Encoding.UTF8.GetString(bytes);
        var entries = new List<UnixPasswdEntry>();
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(':');
            if (parts.Length != 7)
                throw new ContainerConfigurationException($"invalid guest '{PasswdPath}' entry at line {lineNumber}.");
            if (string.IsNullOrWhiteSpace(parts[0]))
                throw new ContainerConfigurationException($"invalid guest '{PasswdPath}' entry at line {lineNumber}.");

            entries.Add(new UnixPasswdEntry(
                parts[0],
                ParseNumericId(parts[2], "uid", PasswdPath, lineNumber),
                ParseNumericId(parts[3], "gid", PasswdPath, lineNumber),
                parts[5]));
        }

        return entries;
    }

    private static List<UnixGroupEntry>? ReadGroupEntries(SyscallManager syscalls)
    {
        var bytes = ReadGuestFileBytes(syscalls, GroupPath);
        if (bytes == null)
            return null;

        var text = Encoding.UTF8.GetString(bytes);
        var entries = new List<UnixGroupEntry>();
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(':');
            if (parts.Length != 4)
                throw new ContainerConfigurationException($"invalid guest '{GroupPath}' entry at line {lineNumber}.");
            if (string.IsNullOrWhiteSpace(parts[0]))
                throw new ContainerConfigurationException($"invalid guest '{GroupPath}' entry at line {lineNumber}.");

            var members = string.IsNullOrWhiteSpace(parts[3])
                ? Array.Empty<string>()
                : parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entries.Add(new UnixGroupEntry(
                parts[0],
                ParseNumericId(parts[2], "gid", GroupPath, lineNumber),
                members));
        }

        return entries;
    }

    private static byte[]? ReadGuestFileBytes(SyscallManager syscalls, string guestPath)
    {
        var (loc, _) = syscalls.ResolvePath(guestPath);
        if (!loc.IsValid || loc.Dentry?.Inode == null)
            return null;
        if (loc.Dentry.Inode.Type != InodeType.File)
            throw new ContainerConfigurationException($"guest path '{guestPath}' is not a regular file.");

        using var file = new LinuxFile(loc.Dentry, FileFlags.O_RDONLY, loc.Mount!);
        using var ms = new MemoryStream();
        var offset = 0L;
        var buffer = new byte[4096];
        while (true)
        {
            var read = loc.Dentry.Inode.ReadToHost(null, file, buffer, offset);
            if (read < 0)
                throw new ContainerConfigurationException($"failed to read guest file '{guestPath}'.");
            if (read == 0)
                break;

            ms.Write(buffer, 0, read);
            offset += read;
        }

        return ms.ToArray();
    }

    private static int ParseNumericId(string raw, string label, string? filePath = null, int? lineNumber = null)
    {
        if (IsNumericId(raw) && int.TryParse(raw, out var value) && value >= 0)
            return value;

        if (filePath != null && lineNumber != null)
            throw new ContainerConfigurationException($"invalid {label} in guest '{filePath}' at line {lineNumber}.");
        throw new ContainerConfigurationException($"invalid {label} '{raw}'.");
    }

    private static bool IsNumericId(string raw)
    {
        return !string.IsNullOrWhiteSpace(raw) && raw.All(static ch => ch is >= '0' and <= '9');
    }

    private static string NormalizeHome(string homeDirectory)
    {
        return string.IsNullOrWhiteSpace(homeDirectory) ? "/" : homeDirectory;
    }

    private sealed record UnixPasswdEntry(string UserName, int Uid, int PrimaryGid, string HomeDirectory);

    private sealed record UnixGroupEntry(string GroupName, int Gid, IReadOnlyList<string> Members);
}
