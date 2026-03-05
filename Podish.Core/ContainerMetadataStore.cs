using System.Text.Json;

namespace Podish.Core;

public sealed class PodishContainerMetadata
{
    public string ContainerId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Image { get; set; } = string.Empty;
    public string State { get; set; } = "created";
    public bool HasTerminal { get; set; }
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public PodishRunSpec Spec { get; set; } = new();
}

public static class PodishContainerMetadataStore
{
    public const string MetadataFileName = "container.json";

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        if (!char.IsLetterOrDigit(name[0]))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                continue;
            return false;
        }

        return true;
    }

    public static List<PodishContainerMetadata> ReadAll(string containersDir)
    {
        var result = new List<PodishContainerMetadata>();
        if (!Directory.Exists(containersDir))
            return result;

        foreach (var dir in Directory.GetDirectories(containersDir))
        {
            var path = Path.Combine(dir, MetadataFileName);
            if (!File.Exists(path))
                continue;

            try
            {
                var metadata = JsonSerializer.Deserialize(File.ReadAllText(path),
                    PodishJsonContext.Default.PodishContainerMetadata);
                if (metadata != null && !string.IsNullOrWhiteSpace(metadata.ContainerId))
                    result.Add(metadata);
            }
            catch
            {
                // ignore malformed metadata
            }
        }

        return result;
    }

    public static PodishContainerMetadata? Resolve(string containersDir, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var all = ReadAll(containersDir);
        var byId = all.FirstOrDefault(x => string.Equals(x.ContainerId, query, StringComparison.Ordinal));
        if (byId != null)
            return byId;
        return all.FirstOrDefault(x => string.Equals(x.Name, query, StringComparison.Ordinal));
    }

    public static void Write(string containersDir, PodishContainerMetadata metadata)
    {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (string.IsNullOrWhiteSpace(metadata.ContainerId))
            throw new ArgumentException("ContainerId is required.", nameof(metadata));

        var containerDir = Path.Combine(containersDir, metadata.ContainerId);
        Directory.CreateDirectory(containerDir);
        var path = Path.Combine(containerDir, MetadataFileName);
        if (metadata.CreatedAt == default)
            metadata.CreatedAt = DateTimeOffset.UtcNow;
        metadata.UpdatedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, PodishJsonContext.Default.PodishContainerMetadata));
    }

    public static void Delete(string containersDir, string containerId)
    {
        var dir = Path.Combine(containersDir, containerId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }
}
