using ProcessTracker.Cli;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;

namespace ProcessTracker.Cli.Tests;

public class KillGuardTests
{
    private static readonly Attribution SessionA = new(AttributionKind.Session, "unit-a", "claude-a", false);

    private static ProcessViewDto Proc(int pid, string? unit) => new(
        $"{pid}@1", pid, null, "x.exe", null,
        unit is null ? new AttributionDto(AttributionKind.Unattributed, null, null, false)
                     : new AttributionDto(AttributionKind.Session, unit, null, false),
        DateTime.UtcNow, DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, null, null,
        unit is null ? ProcessState.Orphan : ProcessState.Normal, false);

    [Fact]
    public void SessionCaller_OwnProcess_Allowed()
    {
        var d = KillGuard.Check(SessionA, Proc(10, "unit-a"), false, []);
        Assert.True(d.Allowed);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void SessionCaller_OtherSessionOrphan_Refused()
    {
        var d = KillGuard.Check(SessionA, Proc(10, null), false, []);
        Assert.False(d.Allowed);
        Assert.Contains("tray", d.Reason);
        Assert.Contains("outside Claude", d.Reason);

        Assert.False(KillGuard.Check(SessionA, Proc(11, "unit-b"), false, []).Allowed);
    }

    [Fact]
    public void SessionCaller_TreeContainsForeignProcess_Refused()
    {
        var members = new[] { Proc(11, "unit-a"), Proc(12, "unit-b") };
        Assert.False(KillGuard.Check(SessionA, Proc(10, "unit-a"), true, members).Allowed);
        // Members only matter when the whole tree is killed.
        Assert.True(KillGuard.Check(SessionA, Proc(10, "unit-a"), false, members).Allowed);
    }

    [Fact]
    public void TerminalOutsideClaude_Allowed()
    {
        Assert.True(KillGuard.Check(null, Proc(10, "unit-b"), true, [Proc(11, null)]).Allowed);
        Assert.True(KillGuard.Check(Attribution.Unattributed, Proc(10, "unit-b"), false, []).Allowed);
        Assert.True(KillGuard.Check(new Attribution(AttributionKind.ClaudeApp, null, null, false), Proc(10, null), false, []).Allowed);
    }
}
