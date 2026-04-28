using System.Text;

namespace Fiberish.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private static readonly SemaphoreSlim ConsoleErrorGate = new(1, 1);
    private static readonly HashSet<string> DeferredDeleteDirectories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Lock DeferredDeleteGate = new();

    static TestWorkspace()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushDeferredDeletes();
    }

    public TestWorkspace(string prefix)
    {
        RootPath = CreateUniqueDirectory(prefix);
    }

    public string RootPath { get; }

    public string GetPath(params string[] relativeSegments)
    {
        ArgumentNullException.ThrowIfNull(relativeSegments);
        return Path.Combine([RootPath, .. relativeSegments]);
    }

    public string CreateDirectory(params string[] relativeSegments)
    {
        var path = GetPath(relativeSegments);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        DeleteDirectory(RootPath);
    }

    public static string CreateUniqueDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), BuildUniqueName(prefix));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string CreateUniqueFilePath(string prefix, string extension = "")
    {
        return Path.Combine(Path.GetTempPath(), BuildUniqueName(prefix, extension));
    }

    public static string ResolveHelloStaticPath()
    {
        return ResolveGuestAssetPath("hello_static", "tests/linux/hello_static");
    }

    public static string ResolveLinuxGuestRoot()
    {
        return Path.GetDirectoryName(ResolveHelloStaticPath())
            ?? throw new InvalidOperationException("Could not determine tests/linux root.");
    }

    public static string ResolveGuestAssetPath(string fileName, string? fallbackRelativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "linux", "assets", fileName);
            if (File.Exists(candidate))
                return candidate;

            if (!string.IsNullOrWhiteSpace(fallbackRelativePath))
            {
                candidate = Path.Combine(current.FullName, fallbackRelativePath);
                if (File.Exists(candidate))
                    return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate guest asset '{fileName}' from test working directory.");
    }

    public static string ReadAllTextShared(string path, Encoding? encoding = null)
    {
        using var stream = OpenReadShared(path);
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static byte[] ReadAllBytesShared(string path)
    {
        using var stream = OpenReadShared(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static FileStream OpenReadShared(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            DeleteDirectory(path);
            return;
        }

        if (File.Exists(path))
            DeleteFile(path);
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        if (!OperatingSystem.IsWindows())
        {
            Directory.Delete(path, true);
            return;
        }

        RetryDelete(
            () =>
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            },
            path);
    }

    public static void DeleteDirectoryOrDeferOnProcessExit(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            RegisterDeferredDelete(path);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            RegisterDeferredDelete(path);
        }
    }

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            return;
        }

        RetryDelete(
            () =>
            {
                if (File.Exists(path))
                    File.Delete(path);
            },
            path);
    }

    public static async Task<T> RedirectConsoleErrorAsync<T>(Func<StringWriter, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var capture = new StringWriter();
        await ConsoleErrorGate.WaitAsync();
        try
        {
            var previous = Console.Error;
            try
            {
                Console.SetError(capture);
                return await action(capture);
            }
            finally
            {
                Console.SetError(previous);
            }
        }
        finally
        {
            ConsoleErrorGate.Release();
            capture.Dispose();
        }
    }

    private static string BuildUniqueName(string prefix, string extension = "")
    {
        var sanitizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? "fiberish-test-"
            : new string(prefix.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());

        if (!string.IsNullOrEmpty(extension) && extension[0] != '.')
            extension = "." + extension;

        return $"{sanitizedPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}{extension}";
    }

    private static void RetryDelete(Action deleteAction, string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 400; attempt++)
        {
            try
            {
                deleteAction();
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
        }

        if (lastError != null)
            throw lastError;

        throw new IOException($"Failed to delete '{path}'.");
    }

    private static void RegisterDeferredDelete(string path)
    {
        lock (DeferredDeleteGate)
        {
            DeferredDeleteDirectories.Add(path);
        }
    }

    private static void FlushDeferredDeletes()
    {
        string[] paths;
        lock (DeferredDeleteGate)
        {
            paths = DeferredDeleteDirectories.ToArray();
            DeferredDeleteDirectories.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Best-effort process-exit cleanup only.
            }
        }
    }
}
