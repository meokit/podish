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
                _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                {
                    builder.AddConsole(options =>
                    {
                        options.LogToStandardErrorThreshold = LogLevel.Trace; // Send all logs to stderr
                    });
                    builder.SetMinimumLevel(LogLevel.Information);
                });
            }
            return _loggerFactory;
        }
        set => _loggerFactory = value;
    }

    public static ILogger CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
    public static ILogger CreateLogger(string categoryName) => LoggerFactory.CreateLogger(categoryName);
}
