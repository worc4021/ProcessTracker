using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Native;

namespace ProcessTracker.Service;

/// <summary>What the API needs from the running tracker. Every member is safe to call from any thread.</summary>
public interface ITrackerHost
{
    TrackerView View { get; }

    /// <summary>The last <paramref name="n"/> tick frames, oldest first.</summary>
    IReadOnlyList<TickFrame> Recent(int n);

    /// <summary>History records with timestamps in [from, to], oldest first.</summary>
    IEnumerable<SegmentRecord> History(DateTime from, DateTime to);

    void RecordKill(KillAudit a);

    DateTime StartedUtc { get; }
}

/// <summary>
/// Owns the <see cref="Tracker"/> over the native snapshot source and inspector. <see cref="Restore"/> and
/// <see cref="Tick"/> are for <see cref="CollectorWorker"/> only; it disposes this host after its loop has stopped.
/// </summary>
public sealed class TrackerHost : ITrackerHost, IDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    private readonly Tracker _tracker;
    private readonly string _historyDir;
    private readonly ILogger<TrackerHost> _log;
    private readonly object _tickGate = new(); // held by Restore/Tick, so Dispose never runs mid-tick
    private bool _disposed;

    public TrackerHost(ServiceOptions options, TimeProvider time, ILogger<Tracker> log, ILogger<TrackerHost> hostLog)
    {
        _log = hostLog;
        _tracker = new Tracker(new NtSnapshotSource(), new PebProcessInspector(), new TrackerOptions
        {
            DataDir = options.DataDir,
            Retention = options.Retention,
            OrphanIdleAlert = options.OrphanIdleAlert,
            OrphanAlertImages = options.OrphanAlertImages,
            Activity = options.ToActivityOptions(),
            UserProfilePath = options.UserProfilePath,
        }, log, time, new ProfileResolver());
        _historyDir = Path.Combine(options.DataDir, "history");
        StartedUtc = time.GetUtcNow().UtcDateTime;
    }

    public DateTime StartedUtc { get; }

    public TrackerView View => _tracker.View;

    public void Restore()
    {
        lock (_tickGate)
            if (!_disposed) _tracker.Restore();
    }

    /// <summary>Runs one tick; does nothing once disposed.</summary>
    public void Tick()
    {
        lock (_tickGate)
            if (!_disposed) _tracker.Tick();
    }

    public IReadOnlyList<TickFrame> Recent(int n)
    {
        var all = _tracker.Ring.Since(DateTime.MinValue);
        return all.Count <= n ? all : all.Skip(all.Count - n).ToList();
    }

    /// <summary>
    /// Segment files up to the oldest frame still in the in-memory ring, then the ring itself: the last 10 minutes
    /// are at full resolution and include frames the segment writer has not flushed yet.
    /// </summary>
    public IEnumerable<SegmentRecord> History(DateTime from, DateTime to)
    {
        var ring = _tracker.Ring.Since(DateTime.MinValue);
        var ringStart = ring.Count > 0 ? ring[0].TimestampUtc : DateTime.MaxValue;

        if (from < ringStart)
        {
            var segmentTo = to < ringStart ? to : ringStart.AddTicks(-1);
            foreach (var r in SegmentReader.Read(_historyDir, from, segmentTo)) yield return r;
        }

        foreach (var f in ring)
        {
            if (f.TimestampUtc < from || f.TimestampUtc > to) continue;
            yield return new SystemRec(f.System);
            foreach (var s in f.Samples) yield return new ProcessRec(s);
            foreach (var a in f.Sessions) yield return new SessionRec(a);
        }
    }

    public void RecordKill(KillAudit a) => _tracker.State.RecordKill(a);

    /// <summary>
    /// Flushes history segments and closes the state store. Idempotent. Waits up to 5 s for an in-flight tick and
    /// refuses (leaving the tracker open) if it is still running, since disposing mid-tick would corrupt its state.
    /// </summary>
    public void Dispose()
    {
        if (!Monitor.TryEnter(_tickGate, DisposeWait))
        {
            _log.LogError("A tick is still running after {Seconds} s; history and state not flushed", DisposeWait.TotalSeconds);
            return;
        }
        try
        {
            if (_disposed) return;
            _disposed = true;
            _tracker.Dispose();
        }
        finally
        {
            Monitor.Exit(_tickGate);
        }
    }
}
