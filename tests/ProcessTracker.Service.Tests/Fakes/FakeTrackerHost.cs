using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Service.Tests.Fakes;

/// <summary>An <see cref="ITrackerHost"/> whose view and history the test sets; records history queries and kills.</summary>
public sealed class FakeTrackerHost : ITrackerHost
{
    private readonly object _gate = new();

    public TrackerView View { get; set; } = TrackerView.Empty;

    public DateTime StartedUtc { get; set; }

    public List<TickFrame> Frames { get; } = [];

    /// <summary>What <see cref="History"/> serves, filtered to the requested range.</summary>
    public List<SegmentRecord> Records { get; } = [];

    public List<(DateTime From, DateTime To)> HistoryCalls { get; } = [];

    public List<KillAudit> Kills { get; } = [];

    public IReadOnlyList<TickFrame> Recent(int n) => Frames.TakeLast(n).ToList();

    public IEnumerable<SegmentRecord> History(DateTime from, DateTime to)
    {
        lock (_gate) HistoryCalls.Add((from, to));
        return Records.Where(r => Timestamp(r) is var ts && ts >= from && ts <= to).ToList();
    }

    public void RecordKill(KillAudit a)
    {
        lock (_gate) Kills.Add(a);
    }

    private static DateTime Timestamp(SegmentRecord r) => r switch
    {
        SystemRec s => s.Value.TimestampUtc,
        ProcessRec p => p.Value.TimestampUtc,
        ProcessInfoRec i => i.Value.FirstSeenUtc,
        SessionRec s => s.Value.TimestampUtc,
        _ => default,
    };
}

/// <summary>A clock frozen at a given instant.</summary>
public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
