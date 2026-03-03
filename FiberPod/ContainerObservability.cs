using System.Text;
using System.Text.Json;
using Fiberish.Core.VFS.TTY;

namespace FiberPod;

internal enum ContainerLogDriver
{
    JsonFile,
    None
}

internal static class ContainerLogDriverParser
{
    public static bool TryParse(string? value, out ContainerLogDriver driver)
    {
        if (string.Equals(value, "json-file", StringComparison.OrdinalIgnoreCase))
        {
            driver = ContainerLogDriver.JsonFile;
            return true;
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            driver = ContainerLogDriver.None;
            return true;
        }

        driver = default;
        return false;
    }
}

internal interface IContainerLogSink : IDisposable
{
    void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer);
}

internal sealed class NoneContainerLogSink : IContainerLogSink
{
    public void Dispose()
    {
    }

    public void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
    }
}

internal sealed class JsonFileContainerLogSink : IContainerLogSink
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public JsonFileContainerLogSink(string logPath)
    {
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    public void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0) return;

        var entry = new ContainerLogEntry(
            DateTimeOffset.UtcNow,
            kind == TtyEndpointKind.Stderr ? "stderr" : "stdout",
            Encoding.UTF8.GetString(buffer));
        var json = JsonSerializer.Serialize(entry);

        lock (_gate)
        {
            _writer.WriteLine(json);
        }
    }
}

internal sealed record ContainerLogEntry(DateTimeOffset Time, string Stream, string Log);

internal sealed record ContainerEvent(
    DateTimeOffset Time,
    string Type,
    string ContainerId,
    string? Image = null,
    int? ExitCode = null,
    string? Message = null);

internal sealed class ContainerEventStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public ContainerEventStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
    }

    public void Append(ContainerEvent evt)
    {
        var json = JsonSerializer.Serialize(evt);
        lock (_gate)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public IEnumerable<ContainerEvent> ReadAll()
    {
        if (!File.Exists(_path)) yield break;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            ContainerEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ContainerEvent>(line);
            }
            catch
            {
                continue;
            }

            if (evt != null) yield return evt;
        }
    }
}
