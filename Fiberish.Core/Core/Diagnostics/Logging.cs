using Microsoft.Extensions.Logging;

namespace Bifrost.Diagnostics;

public static class Logging
{
    private static ILoggerFactory? _loggerFactory;

    public static ILoggerFactory LoggerFactory
    {
        get
        {
            if (_loggerFactory == null)
            {
                return new LoggerFactory();
            }
            return _loggerFactory;
        }
        set => _loggerFactory = value;
    }

    public static ILogger CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
    public static ILogger CreateLogger(string categoryName) => LoggerFactory.CreateLogger(categoryName);

    /// <summary>
    /// Creates a logger for a Task with PID and TID scope embedded in the category name.
    /// </summary>
    public static ILogger CreateTaskLogger(int pid, int tid) => 
        LoggerFactory.CreateLogger($"Task[{pid}:{tid}]");
    
    /// <summary>
    /// Creates a logger for a Task with PID and TID scope embedded in the category name.
    /// </summary>
    public static ILogger CreateTaskLogger<T>(int pid, int tid) => 
        LoggerFactory.CreateLogger($"{typeof(T).Name}[{pid}:{tid}]");
}
