namespace ProcessTracker.Core.Model;

public sealed record Snapshot(DateTime TimestampUtc, IReadOnlyList<RawProcessCounters> Processes, SystemCounters System);
