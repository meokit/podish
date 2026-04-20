using System.Text.Json;

namespace Podish.Core;

public static class ContainerLaunchSpecResolver
{
    public const string DefaultShellPath = "/bin/sh";
    public const string DefaultPathValue = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    public static bool IsRootfsMode(PodishRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return !string.IsNullOrWhiteSpace(spec.Rootfs);
    }

    public static bool NeedsLegacyNormalization(PodishRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return !IsRootfsMode(spec) && string.IsNullOrWhiteSpace(spec.Exe);
    }

    public static string ResolveExistingRootfsPath(string ociStoreImagesDir, PodishRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(ociStoreImagesDir);
        ArgumentNullException.ThrowIfNull(spec);

        if (IsRootfsMode(spec))
        {
            var rootfsPath = spec.Rootfs ?? string.Empty;
            if (!Directory.Exists(rootfsPath))
                throw new DirectoryNotFoundException($"rootfs path not found: {rootfsPath}");
            return rootfsPath;
        }

        if (string.IsNullOrWhiteSpace(spec.Image))
            throw new InvalidOperationException("image is required unless rootfs is set");

        var rootfsPathForImage = spec.Image;
        var ociStoreDir = Path.Combine(ociStoreImagesDir, ToSafeImageName(spec.Image));
        if (!Directory.Exists(rootfsPathForImage) && Directory.Exists(ociStoreDir))
            rootfsPathForImage = ociStoreDir;

        if (!Directory.Exists(rootfsPathForImage))
            throw new DirectoryNotFoundException($"image store path not found: {rootfsPathForImage}");

        return rootfsPathForImage;
    }

    public static PodishRunSpec ResolveEffectiveSpec(PodishRunSpec spec, string rootfsPath, bool rootfsMode)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootfsPath);

        var imageConfig = rootfsMode ? null : TryReadImageRuntimeConfig(rootfsPath);
        var effectiveUser = string.IsNullOrWhiteSpace(spec.User) ? imageConfig?.User : spec.User;
        var explicitExe = spec.Exe?.Trim();
        string finalExe;
        string[] finalExeArgs;

        if (!string.IsNullOrWhiteSpace(explicitExe))
        {
            finalExe = explicitExe;
            finalExeArgs = spec.ExeArgs ?? Array.Empty<string>();
        }
        else
        {
            (finalExe, finalExeArgs) = ResolveImageCommand(imageConfig);
        }

        var mergedEnv = MergeEnvironmentEntries(imageConfig?.Env, spec.Env);
        var workingDir = NormalizeGuestAbsolutePath(spec.WorkingDir);
        if (string.IsNullOrWhiteSpace(workingDir))
            workingDir = NormalizeGuestAbsolutePath(imageConfig?.WorkingDir);

        return CopySpec(
            spec,
            user: effectiveUser,
            exe: finalExe,
            exeArgs: finalExeArgs,
            env: mergedEnv,
            workingDir: workingDir);
    }

    public static string[] MergeEnvironmentEntries(params IEnumerable<string>?[] layers)
    {
        var flattened = new List<string>();
        foreach (var layer in layers)
        {
            if (layer == null)
                continue;

            foreach (var entry in layer)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                flattened.Add(entry);
            }
        }

        if (flattened.Count == 0)
            return Array.Empty<string>();

        var selected = new List<string>(flattened.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = flattened.Count - 1; i >= 0; i--)
        {
            var entry = flattened[i];
            var key = GetEnvironmentKey(entry);
            if (!seenKeys.Add(key))
                continue;
            selected.Add(entry);
        }

        selected.Reverse();
        return selected.ToArray();
    }

    public static string? TryGetEnvironmentValue(IEnumerable<string>? envs, string key)
    {
        if (envs == null || string.IsNullOrWhiteSpace(key))
            return null;

        string? value = null;
        foreach (var entry in envs)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;
            if (!string.Equals(entry[..separator], key, StringComparison.Ordinal))
                continue;
            value = entry[(separator + 1)..];
        }

        return value;
    }

    public static string NormalizeGuestAbsolutePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        var segments = new List<string>();
        var candidate = rawPath.Replace('\\', '/');
        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static string CombineGuestPath(string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return NormalizeGuestAbsolutePath(baseDirectory);

        if (relativePath.StartsWith("/", StringComparison.Ordinal))
            return NormalizeGuestAbsolutePath(relativePath);

        var normalizedBase = NormalizeGuestAbsolutePath(baseDirectory);
        return NormalizeGuestAbsolutePath(
            normalizedBase == "/"
                ? "/" + relativePath
                : normalizedBase + "/" + relativePath);
    }

    public static string ToSafeImageName(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        return imageReference.Replace("/", "_").Replace(":", "_");
    }

    private static PodishRunSpec CopySpec(
        PodishRunSpec spec,
        string? user = null,
        string? exe = null,
        string[]? exeArgs = null,
        string[]? env = null,
        string? workingDir = null)
    {
        return new PodishRunSpec
        {
            Name = spec.Name,
            Hostname = spec.Hostname,
            AutoRemove = spec.AutoRemove,
            NetworkMode = spec.NetworkMode,
            Image = spec.Image,
            Rootfs = spec.Rootfs,
            FileSystemBackend = spec.FileSystemBackend,
            User = user ?? spec.User,
            Exe = exe ?? spec.Exe,
            ExeArgs = exeArgs ?? spec.ExeArgs,
            Volumes = spec.Volumes,
            Env = env ?? spec.Env,
            Dns = spec.Dns,
            Interactive = spec.Interactive,
            Tty = spec.Tty,
            Strace = spec.Strace,
            Init = spec.Init,
            PulseServer = spec.PulseServer,
            WaylandServer = spec.WaylandServer,
            WaylandDesktopWidth = spec.WaylandDesktopWidth,
            WaylandDesktopHeight = spec.WaylandDesktopHeight,
            MemoryQuotaBytes = spec.MemoryQuotaBytes,
            LogDriver = spec.LogDriver,
            TerminalRows = spec.TerminalRows,
            TerminalCols = spec.TerminalCols,
            PublishedPorts = spec.PublishedPorts,
            WorkingDir = workingDir ?? spec.WorkingDir
        };
    }

    private static (string Exe, string[] ExeArgs) ResolveImageCommand(OciStoredImageRuntimeConfig? imageConfig)
    {
        var entrypoint = imageConfig?.Entrypoint;
        var cmd = imageConfig?.Cmd;

        if (entrypoint is { Length: > 0 })
        {
            ValidateExecutable(entrypoint[0], "image entrypoint");
            var args = new List<string>(Math.Max(0, entrypoint.Length - 1) + (cmd?.Length ?? 0));
            for (var i = 1; i < entrypoint.Length; i++)
                args.Add(entrypoint[i]);
            if (cmd != null)
                args.AddRange(cmd);
            return (entrypoint[0], args.ToArray());
        }

        if (cmd is { Length: > 0 })
        {
            ValidateExecutable(cmd[0], "image command");
            return (cmd[0], cmd.Skip(1).ToArray());
        }

        return (DefaultShellPath, Array.Empty<string>());
    }

    private static void ValidateExecutable(string? exe, string source)
    {
        if (!string.IsNullOrWhiteSpace(exe))
            return;
        throw new InvalidOperationException($"{source} is empty.");
    }

    private static string GetEnvironmentKey(string entry)
    {
        var separator = entry.IndexOf('=');
        return separator <= 0 ? entry : entry[..separator];
    }

    private static OciStoredImageRuntimeConfig? TryReadImageRuntimeConfig(string rootfsPath)
    {
        var imagePath = Path.Combine(rootfsPath, "image.json");
        if (!File.Exists(imagePath))
            return null;

        var image = JsonSerializer.Deserialize(File.ReadAllText(imagePath), PodishJsonContext.Default.OciStoredImage);
        if (image == null)
            return null;

        return new OciStoredImageRuntimeConfig(
            image.ConfigUser,
            image.ConfigEntrypoint,
            image.ConfigCmd,
            image.ConfigEnv,
            image.ConfigWorkingDir);
    }
}

public sealed record OciStoredImageRuntimeConfig(
    string? User = null,
    string[]? Entrypoint = null,
    string[]? Cmd = null,
    string[]? Env = null,
    string? WorkingDir = null);
