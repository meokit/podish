using Microsoft.Extensions.Logging;

namespace Podish.Core;

public sealed class ContainerRuntimeThread : IDisposable
{
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private bool _disposed;
    private Task<int>? _runTask;

    public ContainerRuntimeThread(ILogger logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }

    public Task<int> Start(ContainerRunRequest request)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_runTask != null)
                throw new InvalidOperationException("Container runtime thread has already been started.");

            _runTask = Task.Factory.StartNew(
                static state =>
                {
                    var (logger, loggerFactory, req) =
                        ((ILogger logger, ILoggerFactory loggerFactory, ContainerRunRequest request))state!;
                    var service = new ContainerRuntimeService(logger, loggerFactory);
                    return service.RunAsync(req);
                },
                (_logger, _loggerFactory, request),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            return _runTask;
        }
    }

    public Task<int> WaitAsync()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _runTask ?? throw new InvalidOperationException("Container runtime thread has not been started.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ContainerRuntimeThread));
    }
}