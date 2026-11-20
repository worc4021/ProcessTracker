using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Storage;

/// <summary>Per-session total for one tick (spec section 6), so session history needs no re-summing.</summary>
public readonly record struct SessionAggregate(
    string UnitId, DateTime TimestampUtc, float CpuCores,
    long PrivateBytes, long ReadBytesDelta, long WriteBytesDelta, int ProcessCount);

/// <summary>Everything collected in one sampling tick.</summary>
public sealed record TickFrame(
    DateTime TimestampUtc,
    SystemSample System,
    IReadOnlyList<ProcessSample> Samples,
    IReadOnlyList<SessionAggregate> Sessions);
