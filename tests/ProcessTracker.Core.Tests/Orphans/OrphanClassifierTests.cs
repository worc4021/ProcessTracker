using Attr = ProcessTracker.Core.Model.Attribution;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Tests.Orphans;

public class OrphanClassifierTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private const string Unit = "sid:u1";

    private static ProcessKey Key(int pid) => new(pid, 1000 + pid);

    private static ProcessRecord Rec(ProcessKey k, ProcessKey? parent, bool anchor, string image = "node.exe") =>
        new(k, parent, image, null, null, new Attr(AttributionKind.Session, Unit, "u1", anchor), T, null);

    private static ProcessTable Table(params ProcessKey[] live)
    {
        var t = new ProcessTable();
        t.SetLive(live.Select(k => (k, (ProcessKey?)null)));
        return t;
    }

    private static SessionRegistry LiveRegistry()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), null, true, "claude.exe"));
        return reg;
    }

    [Fact]
    public void EndedSession_IsOrphan()
    {
        var reg = LiveRegistry();
        reg.ProcessExited(Key(1), T);
        Assert.Equal(ProcessState.Orphan, OrphanClassifier.Classify(Rec(Key(2), Key(1), false), reg, Table(Key(2))));
    }

    [Fact]
    public void LiveSession_DeadParent_IsDetached()
    {
        var reg = LiveRegistry();
        Assert.Equal(ProcessState.Detached, OrphanClassifier.Classify(Rec(Key(2), Key(99), false), reg, Table(Key(1), Key(2))));
        Assert.Equal(ProcessState.Detached, OrphanClassifier.Classify(Rec(Key(2), null, false), reg, Table(Key(1), Key(2))));
    }

    [Fact]
    public void LiveSession_LiveParent_IsNormal()
    {
        var reg = LiveRegistry();
        Assert.Equal(ProcessState.Normal, OrphanClassifier.Classify(Rec(Key(2), Key(1), false), reg, Table(Key(1), Key(2))));
    }

    [Fact]
    public void LiveSession_Anchor_IsNormal()
    {
        var reg = LiveRegistry();
        Assert.Equal(ProcessState.Normal, OrphanClassifier.Classify(Rec(Key(1), null, true, "claude.exe"), reg, Table(Key(1))));
    }

    [Fact]
    public void Unattributed_IsNormal()
    {
        var r = new ProcessRecord(Key(5), null, "x.exe", null, null, Attr.Unattributed, T, null);
        Assert.Equal(ProcessState.Normal, OrphanClassifier.Classify(r, new SessionRegistry(), Table(Key(5))));
    }

    private static readonly string[] Watched = ["engine*.exe", "solver.exe"];

    [Fact]
    public void Alertable_DefaultWatchList_AnyIdleOrphan_True() =>
        Assert.True(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "node.exe"), ProcessState.Orphan,
            T.AddMinutes(-31), T, Idle));

    [Fact]
    public void Alertable_EmptyWatchList_AnyIdleOrphan_True() =>
        Assert.True(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "node.exe"), ProcessState.Orphan,
            T.AddMinutes(-31), T, Idle, []));

    [Fact]
    public void Alertable_IdleBelowThreshold_False() =>
        Assert.False(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "engine.exe"), ProcessState.Orphan,
            T.AddMinutes(-10), T, Idle, Watched));

    [Fact]
    public void Alertable_WatchListSet_MatchingImageIdle_True() =>
        Assert.True(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "SOLVER.exe"), ProcessState.Orphan,
            T.AddMinutes(-31), T, Idle, Watched));

    [Fact]
    public void Alertable_WatchListSet_OtherImage_False() =>
        Assert.False(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "node.exe"), ProcessState.Orphan,
            T.AddMinutes(-90), T, Idle, Watched));

    [Fact]
    public void Alertable_WatchListWildcard_True() =>
        Assert.True(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "engine_sweep.exe"), ProcessState.Orphan,
            T.AddMinutes(-31), T, Idle, Watched));

    [Fact]
    public void Alertable_QuestionMarkWildcard_MatchesOneChar()
    {
        string[] w = ["job?.exe"];
        Assert.True(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "job1.exe"), ProcessState.Orphan, T.AddMinutes(-31), T, Idle, w));
        Assert.False(OrphanClassifier.IsAlertable(Rec(Key(2), null, false, "job12.exe"), ProcessState.Orphan, T.AddMinutes(-31), T, Idle, w));
    }

    [Fact]
    public void Alertable_NeverActive_UsesFirstSeen()
    {
        var r = Rec(Key(2), null, false, "engine.exe") with { FirstSeenUtc = T.AddMinutes(-45) };
        Assert.True(OrphanClassifier.IsAlertable(r, ProcessState.Orphan, null, T, Idle));
        Assert.False(OrphanClassifier.IsAlertable(r, ProcessState.Normal, null, T, Idle));
    }
}
