using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Storage;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Tests.Storage;

public sealed class StateStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly (ProcessKey, ActivityState, RawProcessCounters)[] NoLive = [];
    private static readonly SessionInfo[] NoSessions = [];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-state-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");

    public StateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static RawProcessCounters Raw(ProcessKey k, string image = "a.exe") =>
        new(k, 1, image, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 12, 13);

    private static ProcessRecord Rec(int pid, DateTime? exited = null, Attr? attr = null) =>
        new(new ProcessKey(pid, 1000 + pid), new ProcessKey(1, 5), "p" + pid + ".exe", @"C:\p.exe", "p --x",
            attr ?? new Attr(AttributionKind.Session, "u1", "c1", true), T0, exited);

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        return c;
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverything()
    {
        var rec = Rec(7);
        var orphan = new ProcessRecord(new ProcessKey(8, 2008), null, "o.exe", null, null, Attr.Unattributed, T0, T0.AddMinutes(1));
        var act = new ActivityState(T0, null, T0.AddSeconds(1), T0.AddSeconds(2));
        var raw = Raw(rec.Key);
        var session = new SessionInfo("u1", "host1", new[] { "c1", "c2" }, rec.Key, T0, null, @"C:\w", "title", T0.AddSeconds(5));
        var ended = new SessionInfo("u2", null, Array.Empty<string>(), null, T0, T0.AddHours(1), null, null, null);

        using (var s = new StateStore(DbPath))
            s.SaveTick(T0.AddSeconds(9), new[] { rec, orphan }, new[] { (rec.Key, act, raw) }, new[] { session, ended });

        using var s2 = new StateStore(DbPath);
        var st = s2.Load();
        Assert.Equal(T0.AddSeconds(9), st.LastTickUtc);
        Assert.Equal(DateTimeKind.Utc, st.LastTickUtc!.Value.Kind);
        Assert.Equal(2, st.Processes.Count);
        Assert.Equal(rec, st.Processes.Single(p => p.Key == rec.Key));
        Assert.Equal(orphan, st.Processes.Single(p => p.Key == orphan.Key));
        var (la, lr) = st.Live[rec.Key];
        Assert.Equal(act, la);
        Assert.Equal(raw, lr);
        Assert.Equal(2, st.Sessions.Count);
        var ls = st.Sessions.Single(x => x.UnitId == "u1");
        Assert.Equal(new[] { "c1", "c2" }, ls.ClaudeSessionIds.OrderBy(x => x).ToArray());
        Assert.Equal(session with { ClaudeSessionIds = ls.ClaudeSessionIds }, ls);
        var le = st.Sessions.Single(x => x.UnitId == "u2");
        Assert.Empty(le.ClaudeSessionIds);
        Assert.Equal(ended with { ClaudeSessionIds = le.ClaudeSessionIds }, le);
    }

    [Fact]
    public void SaveTick_ReplacesLiveCounters_AndUpsertsRecords()
    {
        using var s = new StateStore(DbPath);
        var a = Rec(1);
        s.SaveTick(T0, new[] { a }, new[] { (a.Key, default(ActivityState), Raw(a.Key)) }, NoSessions);
        var exited = a with { ExitedUtc = T0.AddSeconds(2) };
        s.SaveTick(T0.AddSeconds(2), new[] { exited }, NoLive, NoSessions);
        var st = s.Load();
        Assert.Empty(st.Live);
        Assert.Equal(exited, Assert.Single(st.Processes));
    }

    [Fact]
    public void SaveTick_NullLive_KeepsLiveCountersAndLastTick_StillUpsertsRecordsAndSessions()
    {
        using var s = new StateStore(DbPath);
        var a = Rec(1);
        var raw = Raw(a.Key);
        s.SaveTick(T0, new[] { a }, new[] { (a.Key, default(ActivityState), raw) }, NoSessions);

        var b = Rec(2);
        var session = new SessionInfo("u9", null, new[] { "c9" }, null, T0, null, null, null, null);
        s.SaveTick(T0.AddSeconds(2), new[] { b }, null, new[] { session });

        var st = s.Load();
        Assert.Equal(T0, st.LastTickUtc);
        Assert.Equal(raw, Assert.Single(st.Live).Value.Counters);
        Assert.Equal(new[] { 1, 2 }, st.Processes.Select(p => p.Key.Pid).OrderBy(p => p).ToArray());
        Assert.Equal("u9", Assert.Single(st.Sessions).UnitId);
    }

    [Fact]
    public void CloseOpenRecordsExcept_SetsExitOnlyOnOpenRecordsNotListed()
    {
        using var s = new StateStore(DbPath);
        var running = Rec(1);
        var gone = Rec(2);
        var exited = Rec(3, T0.AddMinutes(-1));
        s.SaveTick(T0, new[] { running, gone, exited }, NoLive, NoSessions);

        s.CloseOpenRecordsExcept(new HashSet<ProcessKey> { running.Key }, T0.AddMinutes(5));

        var recs = s.Load().Processes.ToDictionary(p => p.Key.Pid);
        Assert.Null(recs[1].ExitedUtc);
        Assert.Equal(T0.AddMinutes(5), recs[2].ExitedUtc);
        Assert.Equal(T0.AddMinutes(-1), recs[3].ExitedUtc);
    }

    [Fact]
    public void RecordKill_Persists()
    {
        using (var s = new StateStore(DbPath))
            s.RecordKill(new KillAudit(T0, new ProcessKey(3, 33), "x.exe", "u1", "cli.exe", true, "ok"));
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM kills";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Reopen_IsWalMode()
    {
        using (new StateStore(DbPath)) { }
        using var s = new StateStore(DbPath);
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void OpeningDbWithHigherSchemaVersion_DoesNotThrow()
    {
        var rec = Rec(1);
        using (var s = new StateStore(DbPath))
            s.SaveTick(T0, new[] { rec }, NoLive, NoSessions);

        using (var c = OpenRaw())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value='99' WHERE key='schema_version';" +
                              "ALTER TABLE processes ADD COLUMN future_col TEXT;" +
                              "ALTER TABLE live_counters ADD COLUMN future_col TEXT;" +
                              "ALTER TABLE sessions ADD COLUMN future_col TEXT;";
            cmd.ExecuteNonQuery();
        }

        using var s2 = new StateStore(DbPath);
        var other = Rec(2);
        s2.SaveTick(T0.AddSeconds(2), new[] { other }, new[] { (other.Key, default(ActivityState), Raw(other.Key)) },
            new[] { new SessionInfo("u1", null, new[] { "c" }, null, T0, null, null, null, null) });
        var st = s2.Load();
        Assert.Equal(2, st.Processes.Count);
        Assert.Single(st.Live);
        Assert.Single(st.Sessions);

        using var c2 = OpenRaw();
        using var chk = c2.CreateCommand();
        chk.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        Assert.Equal("99", (string)chk.ExecuteScalar()!);
    }

    [Fact]
    public void PruneExitedBefore_RemovesOnlyExited()
    {
        using var s = new StateStore(DbPath);
        var live = Rec(1);
        var oldExit = Rec(2, T0.AddMinutes(-10));
        var newExit = Rec(3, T0.AddMinutes(10));
        s.SaveTick(T0, new[] { live, oldExit, newExit }, NoLive, NoSessions);
        s.PruneExitedBefore(T0);
        Assert.Equal(new[] { 1, 3 }, s.Load().Processes.Select(p => p.Key.Pid).OrderBy(p => p).ToArray());
    }

    [Fact]
    [Trait("Category", "Perf")] // timing-sensitive: deploy.ps1 and CI leave it out
    public void SaveTick_1000Processes_Under30ms()
    {
        using var s = new StateStore(DbPath);
        var recs = Enumerable.Range(1, 1000).Select(i => Rec(i)).ToArray();
        var live = recs.Select(r => (r.Key, new ActivityState(T0, T0, null, null), Raw(r.Key))).ToArray();
        var sessions = new[] { new SessionInfo("u1", "h", new[] { "c1" }, recs[0].Key, T0, null, "cwd", "t", T0) };

        s.SaveTick(T0, recs, live, sessions); // warm-up
        var sw = Stopwatch.StartNew();
        s.SaveTick(T0.AddSeconds(2), recs, live, sessions);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 30, $"SaveTick took {sw.ElapsedMilliseconds} ms");
    }
}
