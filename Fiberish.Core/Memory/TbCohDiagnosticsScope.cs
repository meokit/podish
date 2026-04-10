using System.Diagnostics;

namespace Fiberish.Memory;

internal readonly record struct TbCohDiagnosticsSnapshot(
    long ApplyWxCalls,
    long ApplyWxFastNoWriters,
    long ApplyWxFastSamePolicy,
    long ApplyWxSlowScans,
    long ApplyWxVisitedWriterPages);

internal sealed class TbCohDiagnosticsScope : IDisposable
{
#if DEBUG
    [ThreadStatic] private static TbCohDiagnosticsScope? _current;

    private readonly TbCohDiagnosticsScope? _previous;
    private long _applyWxCalls;
    private long _applyWxFastNoWriters;
    private long _applyWxFastSamePolicy;
    private long _applyWxSlowScans;
    private long _applyWxVisitedWriterPages;
#endif

    private TbCohDiagnosticsScope()
    {
#if DEBUG
        _previous = _current;
        _current = this;
        IsEnabled = true;
#endif
    }

    public bool IsEnabled { get; }

    public TbCohDiagnosticsSnapshot Snapshot
    {
        get
        {
#if DEBUG
            if (!IsEnabled)
                return default;

            return new TbCohDiagnosticsSnapshot(
                Interlocked.Read(ref _applyWxCalls),
                Interlocked.Read(ref _applyWxFastNoWriters),
                Interlocked.Read(ref _applyWxFastSamePolicy),
                Interlocked.Read(ref _applyWxSlowScans),
                Interlocked.Read(ref _applyWxVisitedWriterPages));
#else
            return default;
#endif
        }
    }

    internal static TbCohDiagnosticsScope Begin()
    {
        return new TbCohDiagnosticsScope();
    }

    [Conditional("DEBUG")]
    internal static void Record(TbCohApplyResult result)
    {
#if DEBUG
        var current = _current;
        if (current == null)
            return;

        Interlocked.Increment(ref current._applyWxCalls);
        switch (result.Kind)
        {
            case TbCohApplyKind.FastNoWriters:
                Interlocked.Increment(ref current._applyWxFastNoWriters);
                return;
            case TbCohApplyKind.FastSamePolicy:
                Interlocked.Increment(ref current._applyWxFastSamePolicy);
                return;
            case TbCohApplyKind.SlowScan:
                Interlocked.Increment(ref current._applyWxSlowScans);
                if (result.VisitedWriterPages != 0)
                    Interlocked.Add(ref current._applyWxVisitedWriterPages, result.VisitedWriterPages);
                return;
            default:
                throw new InvalidOperationException($"Unsupported TbCoh apply result: {result.Kind}.");
        }
#endif
    }

    public void Dispose()
    {
#if DEBUG
        if (!IsEnabled)
            return;

        if (!ReferenceEquals(_current, this))
            throw new InvalidOperationException("TbCoh diagnostics scopes must be disposed in LIFO order.");

        _current = _previous;
#endif
    }
}
