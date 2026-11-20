using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tests.Fakes;
using ProcessTracker.Core.Tracking;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Tests.Tracking;

public sealed class TrackerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private const string ClaudePath = @"C:\Users\u\.local\bin\claude.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-tracker-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSnapshotSource _src = new(T0);
    private readonly FakeInspector _insp = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly List<Tracker> _trackers = new();

    public void Dispose()
    {
        foreach (var t in _trackers) t.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Tracker NewTracker(FakeInspector? inspector = null, string userProfile = "")
    {
        var t = new Tracker(_src, inspector ?? _insp,
            new TrackerOptions { DataDir = _dir, UserProfilePath = userProfile },
            NullLogger<Tracker>.Instance, _time);
        _trackers.Add(t);
        return t;
    }

    /// <summary>Desktop session anchor: claude.exe with only the host session id.</summary>
    private ProcessKey AddAnchor(int pid = 10, long create = 100, string host = "h1")
    {
        var k = _src.Add(pid, create, 1, "claude.exe");
        _insp.Set(k, FakeInspector.Ok(hostSessionId: host, imagePath: ClaudePath));
        return k;
    }

    private void Step(TimeSpan? by = null) => _src.Advance(by ?? TickInterval);

    private void ExecRaw(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "state.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Tick_NewSessionProcess_AppearsWithAttribution()
    {
        var claude = AddAnchor();
        var t = NewTracker();

        var v = t.Tick();

        var p = v.Processes[claude];
        Assert.Equal(new Attr(AttributionKind.Session, "host:h1", null, true), p.Record.Attribution);
        Assert.Equal(ProcessState.Normal, p.State);
        Assert.Equal(T0, p.Record.FirstSeenUtc);
        Assert.Equal(ClaudePath, p.Record.ImagePath);
        var s = Assert.Single(v.Sessions);
        Assert.Equal("host:h1", s.Info.UnitId);
        Assert.Equal(claude, s.Info.Anchor);
        Assert.True(s.IsLive);
        Assert.Equal(1, s.ProcessCount);
        Assert.Same(v, t.View);
    }

    [Fact]
    public void Tick_ChildInheritsFromParentInSameSnapshot()
    {
        // Child listed before its parent: new processes must be handled in create-time order.
        var child = _src.Add(11, 200, 10, "cmd.exe");
        var claude = AddAnchor();
        var t = NewTracker();

        var v = t.Tick();

        var c = v.Processes[child];
        Assert.Equal(claude, c.Record.Parent);
        Assert.Equal(new Attr(AttributionKind.Session, "host:h1", null, false), c.Record.Attribution);
        Assert.Equal(ProcessState.Normal, c.State);
        Assert.Equal(2, Assert.Single(v.Sessions).ProcessCount);
    }

    [Fact]
    public void Tick_AnchorExits_ChildrenBecomeOrphans()
    {
        var claude = AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe");
        var t = NewTracker();
        Assert.Equal(ProcessState.Normal, t.Tick().Processes[matlab].State);

        _src.Remove(claude);
        Step();
        var v = t.Tick();

        Assert.False(v.Processes.ContainsKey(claude));
        Assert.Equal(ProcessState.Orphan, v.Processes[matlab].State);
        var s = Assert.Single(v.Sessions);
        Assert.False(s.IsLive);
        Assert.Equal(T0 + TickInterval, s.Info.EndedUtc);
        Assert.Equal(1, s.ProcessCount);
        Assert.Equal(T0 + TickInterval, t.State.Load().Processes.Single(r => r.Key == claude).ExitedUtc);
    }

    [Fact]
    public void Tick_NestedClaudeExe_DoesNotHijackAnchor()
    {
        // Anchor A runs a shell, which runs `claude mcp get ...`: a nested claude.exe B that inherits the host id but
        // has no session id. B must not become the anchor, so its exit must not end the session.
        var a = AddAnchor();
        var shell = _src.Add(11, 200, 10, "bash.exe");
        _insp.Set(shell, FakeInspector.Ok(sessionId: "s1", hostSessionId: "h1"));
        var t = NewTracker();
        t.Tick();

        var b = _src.Add(12, 300, 11, "claude.exe");
        _insp.Set(b, FakeInspector.Ok(hostSessionId: "h1", imagePath: ClaudePath));
        Step();
        var v1 = t.Tick();
        Assert.False(v1.Processes[b].Record.Attribution.IsAnchor);
        Assert.Equal(a, Assert.Single(v1.Sessions).Info.Anchor);

        _src.Remove(b);
        Step();
        var v2 = t.Tick();

        var s = Assert.Single(v2.Sessions);
        Assert.True(s.IsLive);
        Assert.Equal(a, s.Info.Anchor);
        Assert.Null(s.Info.EndedUtc);
        Assert.Equal(ProcessState.Normal, v2.Processes[a].State);
        Assert.Equal(ProcessState.Normal, v2.Processes[shell].State);
    }

    [Fact]
    public void Tick_AnchorExits_OtherLiveAnchorRevivesSession()
    {
        // Two claude.exe of the same desktop session, both launched from outside it (e.g. overlapping a resume).
        var older = AddAnchor(pid: 10, create: 100);
        var child = _src.Add(11, 200, 10, "MATLAB.exe");
        var newer = AddAnchor(pid: 20, create: 300);
        var t = NewTracker();
        Assert.Equal(newer, Assert.Single(t.Tick().Sessions).Info.Anchor);

        _src.Remove(newer);
        Step();
        var v = t.Tick();

        var s = Assert.Single(v.Sessions);
        Assert.True(s.IsLive);
        Assert.Equal(older, s.Info.Anchor);
        Assert.Null(s.Info.EndedUtc);
        Assert.Equal(ProcessState.Normal, v.Processes[child].State);
        Assert.Equal(older, t.State.Load().Sessions.Single().Anchor);
    }

    [Fact]
    public void Restore_EndedSessionWithLiveAnchor_IsRevived()
    {
        // State left by the bug: the session's anchor is a nested claude.exe that exited, so the session is ended,
        // while the real (older) anchor is still running and recorded with IsAnchor=true.
        var anchor = AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe");
        var nested = new ProcessKey(12, 300);
        var ended = T0 - TimeSpan.FromMinutes(1);
        var unit = new Attr(AttributionKind.Session, "host:h1", null, true);
        Directory.CreateDirectory(_dir);
        using (var store = new ProcessTracker.Core.Storage.StateStore(Path.Combine(_dir, "state.db")))
        {
            store.SaveTick(ended,
            [
                new ProcessRecord(anchor, null, "claude.exe", ClaudePath, null, unit, T0 - TimeSpan.FromHours(1), null),
                new ProcessRecord(matlab, anchor, "MATLAB.exe", null, null, unit with { IsAnchor = false },
                    T0 - TimeSpan.FromMinutes(30), null),
                new ProcessRecord(nested, anchor, "claude.exe", ClaudePath, null, unit,
                    T0 - TimeSpan.FromMinutes(2), ended),
            ], null,
            [new SessionInfo("host:h1", "h1", [], nested, T0 - TimeSpan.FromHours(1), ended, null, null, null)]);
        }

        var t = NewTracker(new FakeInspector());
        t.Restore();
        var v = t.Tick();

        var s = Assert.Single(v.Sessions);
        Assert.True(s.IsLive);
        Assert.Equal(anchor, s.Info.Anchor);
        Assert.Null(s.Info.EndedUtc);
        Assert.Equal(ProcessState.Normal, v.Processes[matlab].State);
        Assert.Equal(ProcessState.Normal, v.Processes[anchor].State);
        var saved = t.State.Load().Sessions.Single();
        Assert.Equal(anchor, saved.Anchor);
        Assert.Null(saved.EndedUtc);
    }

    [Fact]
    public void Tick_Gone_SkippedAndRetriedNextTick()
    {
        var p = _src.Add(20, 300, 1, "node.exe");
        _insp.Set(p, FakeInspector.Status(InspectStatus.Gone), FakeInspector.Ok(sessionId: "s1"));
        var t = NewTracker();

        var v1 = t.Tick();
        Assert.False(v1.Processes.ContainsKey(p));
        Assert.Equal(1, _insp.Calls(p));
        Assert.DoesNotContain(t.State.Load().Processes, r => r.Key == p);

        Step();
        var v2 = t.Tick();
        Assert.Equal(2, _insp.Calls(p));
        Assert.Equal(new Attr(AttributionKind.Session, "sid:s1", "s1", false), v2.Processes[p].Record.Attribution);
        Assert.Equal(T0 + TickInterval, v2.Processes[p].Record.FirstSeenUtc);
    }

    [Theory]
    [InlineData(InspectStatus.AccessDenied)]
    [InlineData(InspectStatus.Unsupported)]
    public void Tick_AccessDenied_InspectedOnlyOnce(InspectStatus status)
    {
        var p = _src.Add(30, 400, 1, "svc.exe");
        _insp.Set(p, FakeInspector.Status(status));
        var t = NewTracker();

        Assert.Equal(Attr.Unattributed, t.Tick().Processes[p].Record.Attribution);
        for (var i = 0; i < 2; i++)
        {
            Step();
            Assert.True(t.Tick().Processes.ContainsKey(p));
        }

        Assert.Equal(1, _insp.Calls(p));
    }

    [Fact]
    public void Restore_ContinuesDeltasAcrossDowntime()
    {
        AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe", cpuTicks: 1_000_000_000);
        var t1 = NewTracker();
        t1.Tick();
        t1.Dispose();

        // Service down for 5 s, during which MATLAB burned 5 s of CPU (one full core).
        Step(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(5));
        _src.SetCpu(matlab, 1_000_000_000 + 50_000_000);
        var insp2 = new FakeInspector();
        var t2 = NewTracker(insp2);
        t2.Restore();
        var v = t2.Tick();

        var p = v.Processes[matlab];
        Assert.Equal(1.0, p.Latest.CpuCores, 0.001);
        Assert.Equal(T0 + TimeSpan.FromSeconds(5), p.Activity.LastCpuAt);
        Assert.Equal(T0, p.Record.FirstSeenUtc);
        Assert.Equal("host:h1", p.Record.Attribution.UnitId);
        Assert.Equal(0, insp2.Calls(matlab));
        Assert.True(Assert.Single(v.Sessions).IsLive);
    }

    [Fact]
    public void Tick_LiveCountersPersistedEvery30s_RecordsEveryTick()
    {
        AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe", cpuTicks: 1_000);
        var t = NewTracker();
        t.Tick();
        Assert.Equal(T0, t.State.Load().LastTickUtc);

        // Ticks inside the 30 s window: new records are saved at once, counters and last tick are not.
        Step();
        _src.SetCpu(matlab, 2_000);
        var late = _src.Add(12, 300, 10, "cmd.exe");
        t.Tick();
        var st = t.State.Load();
        Assert.Equal(T0, st.LastTickUtc);
        Assert.Equal(1_000, st.Live[matlab].Counters.KernelTime + st.Live[matlab].Counters.UserTime);
        Assert.Contains(st.Processes, r => r.Key == late);
        Assert.False(st.Live.ContainsKey(late));

        while (_src.Now < T0 + TimeSpan.FromSeconds(28))
        {
            Step();
            t.Tick();
        }
        Assert.Equal(T0, t.State.Load().LastTickUtc);

        Step(); // T0 + 30 s
        t.Tick();
        st = t.State.Load();
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), st.LastTickUtc);
        Assert.Equal(2_000, st.Live[matlab].Counters.KernelTime + st.Live[matlab].Counters.UserTime);
        Assert.True(st.Live.ContainsKey(late));
    }

    [Fact]
    public void Dispose_PersistsLatestLiveCounters()
    {
        var matlab = _src.Add(11, 200, 1, "MATLAB.exe", cpuTicks: 1_000);
        var t = NewTracker();
        t.Tick();
        Step();
        _src.SetCpu(matlab, 5_000);
        t.Tick();
        t.Dispose();

        using var store = new ProcessTracker.Core.Storage.StateStore(Path.Combine(_dir, "state.db"));
        var st = store.Load();
        Assert.Equal(T0 + TickInterval, st.LastTickUtc);
        Assert.Equal(5_000, st.Live[matlab].Counters.KernelTime + st.Live[matlab].Counters.UserTime);
    }

    [Fact]
    public void Restore_AfterCrash_FirstDeltaSpansFromLastCounterSave()
    {
        AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe", cpuTicks: 1_000_000_000);
        var t1 = NewTracker();
        t1.Tick(); // counters saved at T0
        Step();
        _src.SetCpu(matlab, 1_000_000_000 + 20_000_000); // one core for 2 s; not persisted (throttled)
        t1.Tick();
        // Crash: t1 is never disposed before the restart.

        Step(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(7));
        _src.SetCpu(matlab, 1_000_000_000 + 70_000_000); // one core throughout the 7 s since the last save
        var t2 = NewTracker(new FakeInspector());
        t2.Restore();
        var v = t2.Tick();

        Assert.Equal(1.0, v.Processes[matlab].Latest.CpuCores, 0.001);
    }

    [Fact]
    public void Restore_LoadFails_FirstTickStillClosesRecordsOfVanishedProcesses()
    {
        var keep = _src.Add(41, 510, 1, "keep.exe");
        var gone = _src.Add(42, 520, 1, "dymosim.exe");
        var t1 = NewTracker();
        t1.Tick();
        t1.Dispose();

        ExecRaw("ALTER TABLE live_counters RENAME TO live_hidden"); // Load fails, process records stay writable
        _src.Remove(gone);
        _time.Advance(TimeSpan.FromSeconds(5));
        var restoredAt = _time.UtcNow;
        Step(TimeSpan.FromSeconds(7));
        var t2 = NewTracker(new FakeInspector());
        t2.Restore(); // logs, does not throw
        var v = t2.Tick();
        ExecRaw("ALTER TABLE live_hidden RENAME TO live_counters");

        Assert.True(v.Processes.ContainsKey(keep));
        var records = t2.State.Load().Processes;
        Assert.Equal(restoredAt, records.Single(r => r.Key == gone).ExitedUtc);
        Assert.Null(records.Single(r => r.Key == keep).ExitedUtc);
    }

    [Fact]
    public void Restore_PidReusedWithDifferentCreateTime_TreatsAsNewProcess()
    {
        var old = _src.Add(40, 500, 1, "MATLAB.exe");
        _insp.Set(old, FakeInspector.Ok(sessionId: "s1"));
        var t1 = NewTracker();
        t1.Tick();
        t1.Dispose();

        _src.Remove(old);
        var reused = _src.Add(40, 900, 1, "notepad.exe");
        _time.Advance(TimeSpan.FromSeconds(5));
        var restoredAt = _time.UtcNow;
        Step(TimeSpan.FromSeconds(7));
        var insp2 = new FakeInspector();
        var t2 = NewTracker(insp2);
        t2.Restore();
        var v = t2.Tick();

        Assert.False(v.Processes.ContainsKey(old));
        var p = v.Processes[reused];
        Assert.Equal(1, insp2.Calls(reused));
        Assert.Equal(Attr.Unattributed, p.Record.Attribution);
        Assert.Equal(T0 + TimeSpan.FromSeconds(7), p.Record.FirstSeenUtc);
        Assert.Equal(0f, p.Latest.CpuCores);
        Assert.Equal(restoredAt, t2.State.Load().Processes.Single(r => r.Key == old).ExitedUtc);
    }

    [Fact]
    public void Restore_ProcessGoneDuringDowntime_MarkedExited()
    {
        var p = _src.Add(41, 510, 1, "dymosim.exe");
        var keep = _src.Add(42, 520, 1, "keep.exe");
        var t1 = NewTracker();
        t1.Tick();
        t1.Dispose();

        _src.Remove(p);
        _time.Advance(TimeSpan.FromSeconds(5));
        var restoredAt = _time.UtcNow;
        Step(TimeSpan.FromSeconds(7));
        var t2 = NewTracker(new FakeInspector());
        t2.Restore();
        var v = t2.Tick();

        Assert.False(v.Processes.ContainsKey(p));
        Assert.True(v.Processes.ContainsKey(keep));
        var st = t2.State.Load();
        Assert.Equal(restoredAt, st.Processes.Single(r => r.Key == p).ExitedUtc);
        Assert.Null(st.Processes.Single(r => r.Key == keep).ExitedUtc);
        Assert.False(st.Live.ContainsKey(p));
    }

    [Fact]
    public void Restore_AnchorDiedDuringDowntime_SessionEnded()
    {
        var claude = AddAnchor();
        var matlab = _src.Add(11, 200, 10, "MATLAB.exe");
        var t1 = NewTracker();
        Assert.True(Assert.Single(t1.Tick().Sessions).IsLive);
        t1.Dispose();

        _src.Remove(claude);
        _time.Advance(TimeSpan.FromSeconds(5));
        var restoredAt = _time.UtcNow;
        Step(TimeSpan.FromSeconds(7));
        var t2 = NewTracker(new FakeInspector());
        t2.Restore();
        var v = t2.Tick();

        var s = Assert.Single(v.Sessions);
        Assert.False(s.IsLive);
        Assert.Equal(restoredAt, s.Info.EndedUtc);
        Assert.Equal(ProcessState.Orphan, v.Processes[matlab].State);
    }

    [Fact]
    public void Tick_PreexistingOrphan_AnchorNeverSeen_IsOrphanFirstTick()
    {
        var matlab = _src.Add(50, 600, 4, "MATLAB.exe");
        _insp.Set(matlab, FakeInspector.Ok(sessionId: "ended-session"));
        var t = NewTracker();

        var v = t.Tick();

        var p = v.Processes[matlab];
        Assert.Equal(ProcessState.Orphan, p.State);
        Assert.False(p.Alertable);
        var s = Assert.Single(v.Sessions);
        Assert.False(s.IsLive);
        Assert.Null(s.Info.Anchor);

        // Idle for longer than the alert threshold: a watched orphan becomes alertable.
        Step(TimeSpan.FromMinutes(31));
        Assert.True(t.Tick().Processes[matlab].Alertable);
    }

    [Fact]
    public void View_IsReplacedNotMutated()
    {
        var a = _src.Add(60, 700, 1, "a.exe");
        var b = _src.Add(61, 701, 1, "b.exe");
        var t = NewTracker();
        Assert.Same(TrackerView.Empty, t.View);

        var v1 = t.Tick();
        var aView = v1.Processes[a];

        _src.Remove(b);
        var c = _src.Add(62, 702, 1, "c.exe");
        _insp.Set(c, FakeInspector.Ok(sessionId: "s1"));
        _src.SetCpu(a, 10_000_000);
        Step();
        var v2 = t.Tick();

        Assert.NotSame(v1, v2);
        Assert.Same(v2, t.View);
        Assert.Equal(T0, v1.TimestampUtc);
        Assert.Equal(new HashSet<ProcessKey> { a, b }, v1.Processes.Keys.ToHashSet());
        Assert.Same(aView, v1.Processes[a]);
        Assert.Equal(0f, v1.Processes[a].Latest.CpuCores);
        Assert.Empty(v1.Sessions);
        Assert.Equal(new HashSet<ProcessKey> { a, c }, v2.Processes.Keys.ToHashSet());
        Assert.Single(v2.Sessions);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<ProcessKey, ProcessView>)v1.Processes).Remove(a));
    }

    [Fact]
    public void Tick_NewClaudeSessionId_ReadsTranscriptMetadata()
    {
        var profile = Path.Combine(_dir, "profile");
        var project = Path.Combine(profile, ".claude", "projects", "C--work");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "s1.jsonl"),
            """{"type":"user","cwd":"C:\\work","message":{"content":"fix the bug"}}""" + "\n");
        var p = _src.Add(70, 800, 1, "node.exe");
        _insp.Set(p, FakeInspector.Ok(sessionId: "s1"));
        var t = NewTracker(userProfile: profile);

        var s = Assert.Single(t.Tick().Sessions);

        Assert.Equal(@"C:\work", s.Info.Cwd);
        Assert.Equal("fix the bug", s.Info.Title);
        Assert.NotNull(s.Info.LastTranscriptWriteUtc);
    }

    [Fact]
    public void Tick_TranscriptRefresh_OnlyStatsOnceCwdAndTitleAreKnown()
    {
        var profile = Path.Combine(_dir, "profile");
        var project = Path.Combine(profile, ".claude", "projects", "C--work");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "s1.jsonl");
        File.WriteAllText(file, """{"type":"user","cwd":"C:\\work","message":{"content":"first title"}}""" + "\n");
        AddAnchor();
        var node = _src.Add(11, 200, 10, "node.exe");
        _insp.Set(node, FakeInspector.Ok(sessionId: "s1", hostSessionId: "h1"));
        var t = NewTracker(userProfile: profile);
        Assert.Equal("first title", Assert.Single(t.Tick().Sessions).Info.Title);

        File.WriteAllText(file, """{"type":"user","cwd":"C:\\other","message":{"content":"changed title"}}""" + "\n");
        var written = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        Step(TimeSpan.FromSeconds(61));
        var s = Assert.Single(t.Tick().Sessions).Info;

        Assert.Equal("first title", s.Title); // not re-read
        Assert.Equal(@"C:\work", s.Cwd);
        Assert.Equal(written, s.LastTranscriptWriteUtc);
    }

    [Fact]
    public void Tick_NewClaudeSessionIdInKnownUnit_IsParsedAgain()
    {
        var profile = Path.Combine(_dir, "profile");
        var project = Path.Combine(profile, ".claude", "projects", "C--work");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "s1.jsonl"),
            """{"type":"user","cwd":"C:\\work","message":{"content":"first conversation"}}""" + "\n");
        File.WriteAllText(Path.Combine(project, "s2.jsonl"),
            """{"type":"user","cwd":"C:\\work","message":{"content":"after clear"}}""" + "\n");
        AddAnchor();
        var n1 = _src.Add(11, 200, 10, "node.exe");
        _insp.Set(n1, FakeInspector.Ok(sessionId: "s1", hostSessionId: "h1"));
        var t = NewTracker(userProfile: profile);
        Assert.Equal("first conversation", Assert.Single(t.Tick().Sessions).Info.Title);

        var n2 = _src.Add(12, 300, 10, "node.exe");
        _insp.Set(n2, FakeInspector.Ok(sessionId: "s2", hostSessionId: "h1"));
        Step();

        Assert.Equal("after clear", Assert.Single(t.Tick().Sessions).Info.Title);
    }

    [Fact]
    public void Tick_TranscriptRefresh_RereadsWhileTitleUnknown()
    {
        var profile = Path.Combine(_dir, "profile");
        var project = Path.Combine(profile, ".claude", "projects", "C--work");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "s1.jsonl");
        File.WriteAllText(file, """{"type":"attachment","cwd":"C:\\work"}""" + "\n");
        AddAnchor();
        var node = _src.Add(11, 200, 10, "node.exe");
        _insp.Set(node, FakeInspector.Ok(sessionId: "s1", hostSessionId: "h1"));
        var t = NewTracker(userProfile: profile);
        Assert.Null(Assert.Single(t.Tick().Sessions).Info.Title);

        File.AppendAllText(file, """{"type":"user","message":{"content":"late title"}}""" + "\n");
        Step(TimeSpan.FromSeconds(61));

        Assert.Equal("late title", Assert.Single(t.Tick().Sessions).Info.Title);
    }

    [Fact]
    public void Tick_Maintenance_RunsHourly()
    {
        // Snapshot clock far behind the wall clock, so the exited record is past retention as soon as it exits.
        _src.Now = T0 - TimeSpan.FromHours(30);
        var p = _src.Add(80, 900, 1, "x.exe");
        var t = NewTracker();
        t.Tick();

        _src.Remove(p);
        Step();
        _time.Advance(TimeSpan.FromMinutes(30));
        t.Tick();
        Assert.Contains(t.State.Load().Processes, r => r.Key == p && r.ExitedUtc is not null);

        Step();
        _time.Advance(TimeSpan.FromMinutes(31));
        t.Tick();
        Assert.DoesNotContain(t.State.Load().Processes, r => r.Key == p);
    }

    [Fact]
    public void Tick_ParentPidReusedByNewerProcess_NotTreatedAsParent()
    {
        // The child's real parent (pid 10) died and pid 10 now belongs to a process created after the child.
        var child = _src.Add(11, 200, 10, "MATLAB.exe");
        var impostor = AddAnchor(pid: 10, create: 300);

        var v = NewTracker().Tick();

        var c = v.Processes[child];
        Assert.Null(c.Record.Parent);
        Assert.Equal(Attr.Unattributed, c.Record.Attribution);
        Assert.True(v.Processes.ContainsKey(impostor));
    }

    [Fact]
    public void Tick_InspectorThrows_TickStillPublishes_ProcessRecordedOnce()
    {
        var bad = _src.Add(95, 50, 1, "bad.exe");
        _insp.Throw(bad);
        var claude = AddAnchor();
        var t = NewTracker();

        var v = t.Tick();

        Assert.Same(v, t.View);
        Assert.Equal("host:h1", v.Processes[claude].Record.Attribution.UnitId);
        Assert.Equal(Attr.Unattributed, v.Processes[bad].Record.Attribution);
        Step();
        Assert.True(t.Tick().Processes.ContainsKey(bad));
        Assert.Equal(1, _insp.Calls(bad));
    }

    [Fact]
    public void Tick_SaveFailsOnce_NextTickPersistsEarlierUpserts()
    {
        var a = _src.Add(90, 1000, 1, "a.exe");
        var b = _src.Add(91, 1001, 1, "b.exe");
        var t = NewTracker();
        t.Tick();

        // Make SaveTick fail for one tick, in which b exits and c appears.
        ExecRaw("ALTER TABLE processes RENAME TO processes_hidden");
        _src.Remove(b);
        var c = _src.Add(92, 1002, 1, "c.exe");
        Step();
        var v2 = t.Tick();
        Assert.Same(v2, t.View);
        Assert.Equal(T0 + TickInterval, v2.TimestampUtc);
        ExecRaw("ALTER TABLE processes_hidden RENAME TO processes");

        Step();
        t.Tick();

        var records = t.State.Load().Processes;
        Assert.Equal(T0 + TickInterval, records.Single(r => r.Key == b).ExitedUtc);
        Assert.Equal(T0 + TickInterval, records.Single(r => r.Key == c).FirstSeenUtc);
        Assert.Null(records.Single(r => r.Key == a).ExitedUtc);
    }
}
