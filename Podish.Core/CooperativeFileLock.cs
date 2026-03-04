using System.Security.Cryptography;
using System.Text;

namespace Podish.Core;

/// <summary>
/// Cooperative cross-instance file lock. Callers sharing the same lock file path
/// serialize critical sections across threads/processes.
/// </summary>
public static class CooperativeFileLock
{
    public static IDisposable Acquire(string lockPath, TimeSpan? timeout = null, int retryDelayMs = 25)
    {
        if (string.IsNullOrWhiteSpace(lockPath))
            throw new ArgumentException("lock path is required", nameof(lockPath));

        if (retryDelayMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryDelayMs));

        var lockName = BuildMutexName(lockPath);
        var mutex = new Mutex(initiallyOwned: false, name: lockName);
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            if (mutex.WaitOne(budget))
                return new Releaser(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new Releaser(mutex);
        }

        mutex.Dispose();
        throw new TimeoutException($"Timed out acquiring lock: {lockPath}");
    }

    private static string BuildMutexName(string lockPath)
    {
        var normalized = Path.GetFullPath(lockPath).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hash = Convert.ToHexString(bytes);
        return OperatingSystem.IsWindows() ? $@"Global\podish_{hash}" : $"podish_{hash}";
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var m = Interlocked.Exchange(ref _mutex, null);
            if (m == null) return;
            try
            {
                m.ReleaseMutex();
            }
            catch
            {
            }
            finally
            {
                m.Dispose();
            }
        }
    }
}
