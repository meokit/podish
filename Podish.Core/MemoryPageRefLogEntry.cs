using Fiberish.Memory;

namespace Podish.Core;

internal readonly record struct MemoryPageRefLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Phase,
    string ContainerId,
    string Image,
    MemoryStatsSnapshot Memory,
    HostPageRefStatsSnapshot HostPageRefs,
    BackingPagePoolSegmentStatsSnapshot BackingPagePoolSegments);
