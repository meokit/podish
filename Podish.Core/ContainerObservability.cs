using System.Text.Json;
using Fiberish.Core.VFS.TTY;
using Microsoft.Extensions.Logging;

namespace Podish.Core;

public enum ContainerLogDriver
{
    JsonFile,
    None
}

public static class ContainerLogDriverParser
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

public interface IContainerLogSink : IDisposable
{
    void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer);
}

public sealed class NoneContainerLogSink : IContainerLogSink
{
    public void Dispose()
    {
    }

    public void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
    }
}

public sealed class JsonFileContainerLogSink : IContainerLogSink
{
    private readonly Lock _gate = new();
    private readonly ILogger? _logger;
    private readonly StreamWriter _writer;
    private Utf8JsonWriter? _jsonWriter;

    public JsonFileContainerLogSink(string logPath, ILogger? logger = null)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            AutoFlush = true
        };
    }

    public void Dispose()
    {
        _logger?.LogDebug("Disposing json-file container log sink");
        lock (_gate)
        {
            _writer.Dispose();
        }

        _logger?.LogDebug("Disposed json-file container log sink");
    }

    public void Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0) return;

        lock (_gate)
        {
            if (_jsonWriter == null)
                _jsonWriter = new Utf8JsonWriter(_writer.BaseStream, new JsonWriterOptions { Indented = false });
            else
                _jsonWriter.Reset(_writer.BaseStream);

            _jsonWriter.WriteStartObject();
            _jsonWriter.WriteString("Time", DateTimeOffset.UtcNow);
            _jsonWriter.WriteString("Stream", kind == TtyEndpointKind.Stderr ? "stderr" : "stdout");
            _jsonWriter.WriteString("Log", buffer);
            _jsonWriter.WriteEndObject();
            _jsonWriter.Flush();

            // Write newline directly to stream to avoid StreamWriter buffering issues
            _writer.BaseStream.WriteByte(10);
        }

        _logger?.LogTrace("Wrote container log entry stream={Stream} bytes={Bytes}",
            kind == TtyEndpointKind.Stderr ? "stderr" : "stdout", buffer.Length);
    }
}

public sealed record ContainerLogEntry(DateTimeOffset Time, string Stream, string Log);

public sealed record ContainerEvent(
    DateTimeOffset Time,
    string Type,
    string ContainerId,
    string? Image = null,
    int? ExitCode = null,
    string? Message = null);

public sealed class ContainerEventStore
{
    private readonly Lock _gate = new();
    private readonly string _lockPath;
    private readonly string _path;

    public ContainerEventStore(string path)
    {
        _path = path;
        _lockPath = path + ".lock";
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
    }

    public void Append(ContainerEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, PodishJsonContext.Default.ContainerEvent);
        using var fileLock = CooperativeFileLock.Acquire(_lockPath);
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
                evt = JsonSerializer.Deserialize(line, PodishJsonContext.Default.ContainerEvent);
            }
            catch
            {
                continue;
            }

            if (evt != null) yield return evt;
        }
    }
}
