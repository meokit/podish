namespace Fiberish.Memory;

public readonly record struct BackingPagePoolSegmentStatsSnapshot(
    int SegmentCount,
    int NonFullSegmentCount,
    int EmptySegmentCount,
    int FullSegmentCount,
    long ReservedBytes,
    long LivePages,
    long FreePagesWithinSegments,
    long CreatedSegmentCount,
    long FreedSegmentCount,
    int PeakSegmentCount);
