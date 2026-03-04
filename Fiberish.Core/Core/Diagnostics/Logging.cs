using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;

namespace Fiberish.Diagnostics;

public static class Logging
{
    private static readonly AsyncLocal<ILoggerFactory?> ScopedLoggerFactory = new();

    public static ILoggerFactory CurrentLoggerFactory
    {
        get => ScopedLoggerFactory.Value ?? NullLoggerFactory.Instance;
    }

    public static ILogger CreateLogger<T>()
    {
        return new ScopedForwardingLogger(typeof(T).FullName ?? typeof(T).Name);
    }

    public static ILogger CreateLogger(string categoryName)
    {
        return new ScopedForwardingLogger(categoryName);
    }

    public static IDisposable BeginScope(ILoggerFactory loggerFactory)
    {
        return new LoggerFactoryScope(loggerFactory);
    }

    /// <summary>
    ///     Creates a logger for a Task with PID and TID scope embedded in the category name.
    /// </summary>
    public static ILogger CreateTaskLogger(int pid, int tid)
    {
        return new ScopedForwardingLogger($"Task[{pid}:{tid}]");
    }

    /// <summary>
    ///     Creates a logger for a Task with PID and TID scope embedded in the category name.
    /// </summary>
    public static ILogger CreateTaskLogger<T>(int pid, int tid)
    {
        return new ScopedForwardingLogger($"{typeof(T).Name}[{pid}:{tid}]");
    }

    private sealed class LoggerFactoryScope : IDisposable
    {
        private readonly ILoggerFactory? _previous;

        public LoggerFactoryScope(ILoggerFactory loggerFactory)
        {
            _previous = ScopedLoggerFactory.Value;
            ScopedLoggerFactory.Value = loggerFactory;
        }

        public void Dispose()
        {
            ScopedLoggerFactory.Value = _previous;
        }
    }

    private sealed class ScopedForwardingLogger : ILogger
    {
        private readonly string _categoryName;

        public ScopedForwardingLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return CurrentLoggerFactory.CreateLogger(_categoryName).BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return CurrentLoggerFactory.CreateLogger(_categoryName).IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            CurrentLoggerFactory.CreateLogger(_categoryName).Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
