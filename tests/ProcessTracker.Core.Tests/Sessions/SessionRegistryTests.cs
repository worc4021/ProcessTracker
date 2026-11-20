using Attr = ProcessTracker.Core.Model.Attribution;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tests.Sessions;

public class SessionRegistryTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Unit = "host:local_abc";

    private static ProcessKey Key(int pid) => new(pid, 1000 + pid);

    private static ProcessRecord Rec(ProcessKey k, string? claudeId, bool anchor, DateTime? seen = null) =>
        new(k, null, "claude.exe", null, null,
            new Attr(AttributionKind.Session, Unit, claudeId, anchor), seen ?? T, null);

    [Fact]
    public void IsLive_AnchorObservedAndRunning_True()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), "s1", true));
        Assert.True(reg.IsLive(Unit));
    }

    [Fact]
    public void IsLive_AnchorExited_False()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), "s1", true));
        reg.ProcessExited(Key(1), T.AddMinutes(5));
        Assert.False(reg.IsLive(Unit));
        Assert.Equal(T.AddMinutes(5), reg.Get(Unit)!.EndedUtc);
    }

    [Fact]
    public void IsLive_AnchorNeverObserved_False()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(2), "s1", false));
        Assert.False(reg.IsLive(Unit));
    }

    [Fact]
    public void MultipleClaudeSessionIds_CollectUnderOneHostUnit()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), "s1", true, T.AddMinutes(1)));
        reg.Observe(Rec(Key(2), "s2", false, T));
        reg.Observe(Rec(Key(3), "s1", false, T.AddMinutes(2)));
        var s = Assert.Single(reg.All());
        Assert.Equal("local_abc", s.HostSessionId);
        Assert.Equal(["s1", "s2"], s.ClaudeSessionIds);
        Assert.Equal(Key(1), s.Anchor);
        Assert.Equal(T, s.FirstSeenUtc);
    }

    [Fact]
    public void Observe_NewAnchorAfterExit_IsLiveAgain()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), "s1", true));
        reg.ProcessExited(Key(1), T.AddMinutes(5));
        reg.Observe(Rec(Key(7), "s1", true, T.AddMinutes(10)));
        Assert.True(reg.IsLive(Unit));
        Assert.Equal(Key(7), reg.Get(Unit)!.Anchor);
        Assert.Null(reg.Get(Unit)!.EndedUtc);
    }

    [Fact]
    public void Observe_OlderLiveAnchor_SessionEnded_RevivesWithIt()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), null, true));
        reg.Observe(Rec(Key(7), null, true, T.AddMinutes(1)));
        reg.ProcessExited(Key(7), T.AddMinutes(5));
        Assert.False(reg.IsLive(Unit));

        reg.Observe(Rec(Key(1), null, true));

        Assert.True(reg.IsLive(Unit));
        Assert.Equal(Key(1), reg.Get(Unit)!.Anchor);
        Assert.Null(reg.Get(Unit)!.EndedUtc);
    }

    [Fact]
    public void Observe_OlderLiveAnchor_WhileCurrentAnchorLive_DoesNotReplace()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(7), null, true));
        reg.Observe(Rec(Key(1), null, true));
        Assert.Equal(Key(7), reg.Get(Unit)!.Anchor);
        Assert.True(reg.IsLive(Unit));
    }

    [Fact]
    public void Observe_ExitedAnchorRecord_NeverReplaces()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), null, true));
        reg.ProcessExited(Key(1), T.AddMinutes(5));
        reg.Observe(Rec(Key(9), null, true) with { ExitedUtc = T.AddMinutes(6) });
        Assert.Equal(Key(1), reg.Get(Unit)!.Anchor);
        Assert.False(reg.IsLive(Unit));
    }

    [Fact]
    public void Observe_OldAnchorReobservedAfterResume_StaysLive()
    {
        var reg = new SessionRegistry();
        reg.Observe(Rec(Key(1), "s1", true));
        reg.ProcessExited(Key(1), T.AddMinutes(5));
        reg.Observe(Rec(Key(7), "s1", true, T.AddMinutes(10)));
        reg.Observe(Rec(Key(1), "s1", true) with { ExitedUtc = T.AddMinutes(5) });
        reg.Observe(Rec(Key(1), "s1", true));
        Assert.True(reg.IsLive(Unit));
        Assert.Equal(Key(7), reg.Get(Unit)!.Anchor);
    }
}
