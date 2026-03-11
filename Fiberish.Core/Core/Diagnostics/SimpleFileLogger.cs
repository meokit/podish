using Microsoft.Extensions.Logging;

namespace Fiberish.Diagnostics;

public class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public SimpleFileLoggerProvider(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SimpleFileLogger(categoryName, _writer, _lock);
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    private class SimpleFileLogger(string categoryName, StreamWriter writer, object lockObj) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel}] [{categoryName}] {message}";
            if (exception != null) logLine += Environment.NewLine + exception;

            lock (lockObj)
            {
                writer.WriteLine(logLine);
            }
        }
    }
}

public static class SimpleFileLoggerExtensions
{
    public static ILoggingBuilder AddSimpleFile(this ILoggingBuilder builder, string filePath)
    {
        builder.AddProvider(new SimpleFileLoggerProvider(filePath));
        return builder;
    }
}