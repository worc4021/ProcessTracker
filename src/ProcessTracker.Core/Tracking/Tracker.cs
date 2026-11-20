using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Attribution;
using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Storage;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Tracking;

/// <summary>
/// Runs the sampling pipeline: snapshot, attribute new processes, compute samples and activity, classify,
/// persist, and publish an immutable <see cref="TrackerView"/>. <see cref="Restore"/> and <see cref="Tick"/>
/// must be called from a single thread; <see cref="View"/> and <see cref="Ring"/> may be read from any thread.
/// </summary>
public sealed class Tracker : IDisposable
{
    private const int RingCapacity = 300;
    private const int MaxTranscriptReadsPerTick = 5;
    private static readonly TimeSpan TranscriptRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRetention = TimeSpan.FromHours(2);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often live counters (and the last tick time) are saved; also saved on <see cref="Dispose"/>. Counters are
    /// cumulative, so after a crash the first delta simply spans from the last save to the first new tick.
    /// </summary>
    private static readonly TimeSpan LiveCountersInterval = TimeSpan.FromSeconds(30);

    /// <summary>Per live process: its record and the counters of the previous tick.</summary>
    private sealed class LiveProcess(ProcessRecord record)
    {
        public ProcessRecord Record { get; } = record;
        public RawProcessCounters? Prev { get; set; }
        public DateTime PrevTs { get; set; }
    }

    /// <summary>Persisted state loaded by <see cref="Restore"/>, applied against the first snapshot.</summary>
    /// <remarks><paramref name="LoadFailed"/>: nothing could be loaded, so the first tick only closes the records the
    /// previous run left open whose processes are gone.</remarks>
    private sealed record Pending(
        DateTime RestoredUtc, DateTime? LastTickUtc, Dictionary<ProcessKey, ProcessRecord> Open,
        IReadOnlyDictionary<ProcessKey, (ActivityState Activity, RawProcessCounters Counters)> Live,
        bool LoadFailed = false);

    private readonly ISnapshotSource _source;
    private readonly IProcessInspector _inspector;
    private readonly TrackerOptions _options;
    private readonly ILogger<Tracker> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly Attributor _attributor = new();
    private readonly ActivityTracker _activity;
    private readonly ProcessTable _table = new();
    private readonly SessionRegistry _sessions = new();
    private readonly TranscriptReader? _transcripts;
    private readonly SegmentWriter _writer;

    private readonly Dictionary<ProcessKey, LiveProcess> _live = new();
    private readonly Dictionary<string, SessionInfo> _savedSessions = new();
    private readonly Dictionary<ProcessKey, ProcessRecord> _unsavedRecords = new();
    private readonly Queue<(string Unit, string Id)> _transcriptQueue = new();
    private readonly HashSet<(string Unit, string Id)> _transcriptQueued = new();
    private readonly Dictionary<string, (string Id, DateTime At)> _transcriptRead = new();

    private TrackerView _view = TrackerView.Empty;
    private SystemCounters? _prevSystem;
    private Pending? _pending;
    private bool _ticked;
    private DateTime _lastTickUtc;
    private DateTime? _liveSavedUtc;
    private bool _persistFailing;
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private bool _disposed;

    public Tracker(ISnapshotSource source, IProcessInspector inspector, TrackerOptions options, ILogger<Tracker> log,
        TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.DataDir))
            throw new ArgumentException("TrackerOptions.DataDir is required.", nameof(options));

        _source = source;
        _inspector = inspector;
        _options = options;
        _log = log;
        _time = time;
        _retention = options.Retention < MinRetention ? MinRetention : options.Retention;

        // Per-image lookup must be case-insensitive even if a caller assigned a plain dictionary.
        var activity = options.Activity ?? new ActivityOptions();
        _activity = new ActivityTracker(new ActivityOptions
        {
            Default = activity.Default,
            PerImage = new Dictionary<string, ActivityThresholds>(activity.PerImage, StringComparer.OrdinalIgnoreCase),
        });

        if (!string.IsNullOrEmpty(options.UserProfilePath))
            _transcripts = new TranscriptReader(options.UserProfilePath);

        Directory.CreateDirectory(options.DataDir);
        State = new StateStore(Path.Combine(options.DataDir, "state.db"), log);
        _writer = new SegmentWriter(Path.Combine(options.DataDir, "history"), options.Retention, log);
        Ring = new SampleRing(RingCapacity);
    }

    /// <summary>The last 10 minutes of tick frames. Safe to read from any thread.</summary>
    public SampleRing Ring { get; }

    public StateStore State { get; }

    /// <summary>The latest published view; <see cref="TrackerView.Empty"/> before the first tick.</summary>
    public TrackerView View => Volatile.Read(ref _view);

    /// <summary>Loads persisted state so the first tick continues deltas and attribution. Call once, before the first tick.</summary>
    public void Restore()
    {
        if (_ticked || _pending is not null) throw new InvalidOperationException("Restore must be called once, before the first tick.");

        PersistedState st;
        try { st = State.Load(); }
        catch (Exception e) when (IsNonFatal(e))
        {
            _log.LogError(e, "Loading persisted tracker state failed; starting without it (records of processes that " +
                             "exited meanwhile are closed on the first tick)");
            _pending = new Pending(_time.GetUtcNow().UtcDateTime, null, new(),
                new Dictionary<ProcessKey, (ActivityState, RawProcessCounters)>(), LoadFailed: true);
            return;
        }
        var open = st.Processes.Where(p => p.ExitedUtc is null).ToDictionary(p => p.Key);
        _sessions.Restore(st.Sessions);
        foreach (var s in st.Sessions)
        {
            _savedSessions[s.UnitId] = s;
            foreach (var id in s.ClaudeSessionIds) _transcriptQueued.Add((s.UnitId, id));
        }
        _pending = new Pending(_time.GetUtcNow().UtcDateTime, st.LastTickUtc, open, st.Live);
        _log.LogInformation("Restored {Processes} open process records and {Sessions} sessions (last tick {LastTick:o})",
            open.Count, st.Sessions.Count, st.LastTickUtc);
    }

    public TrackerView Tick()
    {
        var snap = _source.Take();
        var now = snap.TimestampUtc;

        var current = new Dictionary<ProcessKey, RawProcessCounters>(snap.Processes.Count);
        var pidToKey = new Dictionary<int, ProcessKey>(snap.Processes.Count);
        foreach (var p in snap.Processes)
        {
            current[p.Key] = p;
            pidToKey[p.Key.Pid] = p.Key;
        }

        var upserts = new List<ProcessRecord>();
        if (_pending is { } pending) ApplyRestore(pending, current, upserts);
        _pending = null;
        _ticked = true;
        _lastTickUtc = now;

        // New processes, oldest first so a parent is recorded before its children.
        var newRecords = new List<ProcessRecord>();
        foreach (var p in current.Values.Where(p => !_live.ContainsKey(p.Key)).OrderBy(p => p.Key.CreateTimeTicks))
        {
            var rec = Record(p, pidToKey, now);
            if (rec is null) continue;
            newRecords.Add(rec);
            upserts.Add(rec);
        }

        // Exited processes.
        foreach (var key in _live.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            upserts.Add(_live[key].Record with { ExitedUtc = now });
            _live.Remove(key);
            _sessions.ProcessExited(key, now);
            _activity.Forget(key);
        }

        // Samples and activity.
        var samples = new ProcessSample[_live.Count];
        var active = new bool[_live.Count]; // per sample: crossed an activity threshold (decides history writes)
        var n = 0;
        foreach (var (key, lp) in _live)
        {
            var raw = current[key];
            var s = lp.Prev is { } prev ? SampleCalculator.Compute(prev, lp.PrevTs, raw, now) : SampleCalculator.First(raw, now);
            active[n] = _activity.Update(s, raw.ImageName);
            lp.Prev = raw;
            lp.PrevTs = now;
            samples[n++] = s;
        }
        _table.SetLive(_live.Select(kv => (kv.Key, kv.Value.Record.Parent)));

        var system = SampleCalculator.ComputeSystem(_prevSystem ?? snap.System, snap.System, now);
        _prevSystem = snap.System;

        ReadTranscripts(now);

        // Classification and per-session totals.
        var effectiveAll = _activity.EffectiveLastActiveAll(_table);
        var processViews = new Dictionary<ProcessKey, ProcessView>(samples.Length);
        var totals = new Dictionary<string, SessionAggregate>();
        foreach (var s in samples)
        {
            var rec = _live[s.Key].Record;
            var state = OrphanClassifier.Classify(rec, _sessions, _table);
            var effective = effectiveAll[s.Key];
            var alertable = OrphanClassifier.IsAlertable(rec, state, effective, now, _options.OrphanIdleAlert);
            processViews[s.Key] = new ProcessView(rec, s, _activity.Get(s.Key), effective, state, alertable);

            if (rec.Attribution is { Kind: AttributionKind.Session, UnitId: { } unit })
            {
                var a = totals.TryGetValue(unit, out var cur) ? cur : new SessionAggregate(unit, now, 0, 0, 0, 0, 0);
                totals[unit] = a with
                {
                    CpuCores = a.CpuCores + s.CpuCores,
                    PrivateBytes = a.PrivateBytes + s.PrivateBytes,
                    ReadBytesDelta = a.ReadBytesDelta + s.ReadBytesDelta,
                    WriteBytesDelta = a.WriteBytesDelta + s.WriteBytesDelta,
                    ProcessCount = a.ProcessCount + 1,
                };
            }
        }

        var allSessions = _sessions.All();
        var sessionViews = BuildSessionViews(allSessions, totals, now);

        var frame = new TickFrame(now, system, samples, totals.Values.ToArray());
        Ring.Add(frame);
        _writer.Enqueue(frame, newRecords, active);
        Persist(now, upserts, allSessions, forceLive: false);

        // A fresh dictionary per tick, never touched again once published: readers see an immutable snapshot.
        var view = new TrackerView(now, system, new ReadOnlyDictionary<ProcessKey, ProcessView>(processViews), sessionViews);
        Volatile.Write(ref _view, view);

        RunMaintenanceIfDue();
        return view;
    }

    private void ApplyRestore(Pending pending, Dictionary<ProcessKey, RawProcessCounters> current, List<ProcessRecord> upserts)
    {
        if (pending.LoadFailed)
        {
            try { State.CloseOpenRecordsExcept(current.Keys, pending.RestoredUtc); }
            catch (Exception e) when (IsNonFatal(e)) { _log.LogError(e, "Closing records left open by the previous run failed"); }
            return;
        }

        foreach (var (key, rec) in pending.Open)
        {
            if (current.ContainsKey(key))
            {
                var lp = new LiveProcess(rec);
                if (pending.Live.TryGetValue(key, out var persisted))
                {
                    _activity.Seed(key, persisted.Activity);
                    if (pending.LastTickUtc is { } lastTick)
                    {
                        // The first delta spans the downtime, so work done while the service was down is kept.
                        lp.Prev = persisted.Counters;
                        lp.PrevTs = lastTick;
                    }
                }
                _live[key] = lp;
            }
            else
            {
                upserts.Add(rec with { ExitedUtc = pending.RestoredUtc });
                _sessions.ProcessExited(key, pending.RestoredUtc);
            }
        }

        // Sessions whose anchor is not running now ended while the service was down.
        foreach (var s in _sessions.All())
            if (s.EndedUtc is null && s.Anchor is { } anchor && !current.ContainsKey(anchor))
                _sessions.ProcessExited(anchor, pending.RestoredUtc);
    }

    /// <summary>Inspects and attributes a new process; null when it vanished during inspection (retried next tick).</summary>
    private ProcessRecord? Record(RawProcessCounters p, Dictionary<int, ProcessKey> pidToKey, DateTime now)
    {
        ProcessKey? parent = pidToKey.TryGetValue(p.ParentPid, out var pk)
                             && pk != p.Key && pk.CreateTimeTicks <= p.Key.CreateTimeTicks
            ? pk
            : null;

        // AccessDenied/Unsupported are recorded like any other result and, being recorded, never inspected again.
        // An inspector that throws is treated as Unsupported, so one bad process cannot stall every later tick.
        InspectResult inspect;
        try { inspect = _inspector.Inspect(p.Key); }
        catch (Exception e) when (IsNonFatal(e))
        {
            _log.LogWarning(e, "Inspecting process {Key} ({Image}) failed; recording it as unsupported", p.Key, p.ImageName);
            inspect = new InspectResult(InspectStatus.Unsupported, null, null, null, null, null);
        }
        if (inspect.Status == InspectStatus.Gone) return null;

        Attr? parentAttribution = parent is { } k && _live.TryGetValue(k, out var lp) ? lp.Record.Attribution : null;
        var attribution = _attributor.Attribute(p, inspect, parentAttribution);
        var rec = new ProcessRecord(p.Key, parent, p.ImageName, inspect.ImagePath, inspect.CommandLine, attribution, now, null);

        _live[p.Key] = new LiveProcess(rec);
        _sessions.Observe(rec);
        if (_transcripts is not null
            && attribution is { Kind: AttributionKind.Session, UnitId: { } unit, ClaudeSessionId: { } id }
            && _transcriptQueued.Add((unit, id)))
            _transcriptQueue.Enqueue((unit, id));
        return rec;
    }

    /// <summary>At most <see cref="MaxTranscriptReadsPerTick"/> reads: new session ids first, then refreshes of live sessions.</summary>
    private void ReadTranscripts(DateTime now)
    {
        if (_transcripts is null) return;
        var budget = MaxTranscriptReadsPerTick;

        while (budget > 0 && _transcriptQueue.TryDequeue(out var item))
        {
            budget--;
            ReadTranscript(item.Unit, item.Id, now);
        }

        if (budget == 0) return;
        foreach (var s in _sessions.All())
        {
            if (budget == 0) break;
            if (s.ClaudeSessionIds.Count == 0 || !_sessions.IsLive(s.UnitId)) continue;
            var known = _transcriptRead.TryGetValue(s.UnitId, out var last);
            if (known && now - last.At < TranscriptRefresh) continue;
            budget--;
            ReadTranscript(s.UnitId, known ? last.Id : s.ClaudeSessionIds[^1], now);
        }
    }

    /// <summary>
    /// Parses a transcript the first time; a refresh of the same transcript only stats the file once the session's
    /// cwd and title are known.
    /// </summary>
    private void ReadTranscript(string unit, string claudeSessionId, DateTime now)
    {
        var refresh = _transcriptRead.TryGetValue(unit, out var last) && last.Id == claudeSessionId;
        _transcriptRead[unit] = (claudeSessionId, now);
        if (refresh && _sessions.Get(unit) is { Cwd: not null, Title: not null })
        {
            if (_transcripts!.LastWriteUtc(claudeSessionId) is { } written)
                _sessions.ApplyTranscript(unit, new TranscriptInfo(null, null, written));
        }
        else if (_transcripts!.Read(claudeSessionId) is { } info) _sessions.ApplyTranscript(unit, info);
    }

    /// <summary>Sessions that are live, have live processes, or ended within the retention window; newest first.</summary>
    private SessionView[] BuildSessionViews(
        IReadOnlyList<SessionInfo> sessions, Dictionary<string, SessionAggregate> totals, DateTime now)
    {
        var cutoff = now - _retention;
        var views = new List<SessionView>();
        foreach (var s in sessions)
        {
            var live = _sessions.IsLive(s.UnitId);
            var hasTotals = totals.TryGetValue(s.UnitId, out var a);
            if (!live && !hasTotals && (s.EndedUtc ?? s.FirstSeenUtc) < cutoff) continue;
            views.Add(new SessionView(s, live, a.CpuCores, a.PrivateBytes, a.ProcessCount));
        }
        return views.OrderByDescending(v => v.Info.FirstSeenUtc).ThenBy(v => v.Info.UnitId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Saves new and exited records and only the sessions that changed, every tick; the live counters (with the tick
    /// time) every <see cref="LiveCountersInterval"/>, or when <paramref name="forceLive"/>. Whatever a failed save
    /// held is kept (last write per key wins) and retried with the next tick.
    /// </summary>
    private void Persist(DateTime now, List<ProcessRecord> upserts, IReadOnlyList<SessionInfo> sessions, bool forceLive)
    {
        var changed = sessions
            .Where(s => !_savedSessions.TryGetValue(s.UnitId, out var saved) || !saved.Equals(s))
            .ToList();
        foreach (var r in upserts) _unsavedRecords[r.Key] = r;
        var saveLive = forceLive || _liveSavedUtc is not { } last || now < last || now - last >= LiveCountersInterval;
        try
        {
            // _live holds this tick's counters (Prev was just set to them) and _activity this tick's state.
            var live = saveLive ? _live.Select(kv => (kv.Key, _activity.Get(kv.Key), kv.Value.Prev!)) : null;
            State.SaveTick(now, _unsavedRecords.Values.ToList(), live, changed);
            _unsavedRecords.Clear();
            foreach (var s in changed) _savedSessions[s.UnitId] = s;
            if (saveLive) _liveSavedUtc = now;
            if (_persistFailing) _log.LogInformation("State database writes recovered");
            _persistFailing = false;
        }
        catch (Exception e) when (IsNonFatal(e))
        {
            if (!_persistFailing) _log.LogError(e, "Saving tracker state failed; retrying every tick");
            _persistFailing = true;
        }
    }

    private void RunMaintenanceIfDue()
    {
        var wall = _time.GetUtcNow().UtcDateTime;
        if (wall < _nextMaintenanceUtc) return;
        _nextMaintenanceUtc = wall + MaintenanceInterval;

        _writer.EnforceRetention(wall);
        try { State.PruneExitedBefore(wall - _retention); }
        catch (Exception e) when (IsNonFatal(e)) { _log.LogError(e, "Pruning exited process records failed"); }
    }

    private static bool IsNonFatal(Exception e) => e is not (OutOfMemoryException or AccessViolationException);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ticked) Persist(_lastTickUtc, [], _sessions.All(), forceLive: true);
        try { _writer.FlushAsync().Wait(FlushTimeout); }
        catch (AggregateException e) { _log.LogWarning(e, "Flushing history segments on shutdown failed"); }
        _writer.Dispose();
        State.Dispose();
    }
}
