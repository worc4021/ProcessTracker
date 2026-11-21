using ProcessTracker.Core.Attribution;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests.Attribution;

public class AttributorTests
{
    private const string AppPath = @"C:\Program Files\WindowsApps\Claude_6.6395.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
    private const string TerminalPath = @"C:\Users\u\.local\bin\claude.exe";

    private static readonly Attributor Sut = new();

    private static RawProcessCounters Proc(string image, int pid = 100) =>
        new(new ProcessKey(pid, 777), 1, image, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static InspectResult Ok(string? sid = null, string? host = null, string? path = null) =>
        new(InspectStatus.Ok, sid, host, null, path, null);

    private static InspectResult Failed(InspectStatus status, string? path = null) =>
        new(status, null, null, null, path, null);

    private static ProcessTracker.Core.Model.Attribution Session(string unit, string? sid = null) =>
        new(AttributionKind.Session, unit, sid, false);

    [Fact]
    public void HostId_OnClaudeExe_WithoutSessionId_IsAnchor()
    {
        var a = Sut.Attribute(Proc("claude.exe"), Ok(host: "local_x", path: @"C:\x\claude.exe"), null);
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("host:local_x", a.UnitId);
        Assert.Null(a.ClaudeSessionId);
        Assert.True(a.IsAnchor);
    }

    [Fact]
    public void HostId_OnClaudeExe_WithEmptySessionId_IsAnchor_AndSessionIdNormalisedToNull()
    {
        // CLAUDE_CODE_SESSION_ID= (present but empty) comes back from the inspector as "".
        var a = Sut.Attribute(Proc("claude.exe"), Ok(sid: "", host: "local_x", path: @"C:\x\claude.exe"), null);
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "host:local_x", null, true), a);
    }

    [Fact]
    public void Attributor_ClaudeExeChildOfSameSession_IsNotAnchor()
    {
        // A nested `claude ...` run from inside the session inherits the host id but is not the session's anchor.
        var a = Sut.Attribute(Proc("claude.exe"), Ok(host: "local_x", path: @"C:\x\claude.exe"),
            Session("host:local_x", "sid1"));
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "host:local_x", null, false), a);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unattributed")]
    [InlineData("desktop-app")]
    [InlineData("other-session")]
    public void Attributor_ClaudeExeWithHostId_ParentOutsideSession_IsAnchor(string parentKind)
    {
        var parent = parentKind switch
        {
            "unattributed" => ProcessTracker.Core.Model.Attribution.Unattributed,
            "desktop-app" => new ProcessTracker.Core.Model.Attribution(AttributionKind.ClaudeApp, null, null, false),
            "other-session" => Session("host:other", "sid1"),
            _ => null,
        };
        var a = Sut.Attribute(Proc("claude.exe"), Ok(host: "local_x", path: @"C:\x\claude.exe"), parent);
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "host:local_x", null, true), a);
    }

    [Fact]
    public void HostId_OnChild_WithEmptySessionId_HasNullSessionId()
    {
        var a = Sut.Attribute(Proc("node.exe"), Ok(sid: "", host: "local_x"), null);
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "host:local_x", null, false), a);
    }

    [Fact]
    public void HostAndSessionId_OnPython_IsSessionNotAnchor()
    {
        var a = Sut.Attribute(Proc("python.exe"), Ok(sid: "sid1", host: "local_x"), null);
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("host:local_x", a.UnitId);
        Assert.Equal("sid1", a.ClaudeSessionId);
        Assert.False(a.IsAnchor);
    }

    [Fact]
    public void SessionIdOnly_InheritsParentUnit()
    {
        var a = Sut.Attribute(Proc("node.exe"), Ok(sid: "sid1"), Session("term:1@2"));
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("term:1@2", a.UnitId);
        Assert.Equal("sid1", a.ClaudeSessionId);
        Assert.False(a.IsAnchor);
    }

    [Fact]
    public void SessionIdOnly_NoParent_UsesSidUnit()
    {
        var a = Sut.Attribute(Proc("node.exe"), Ok(sid: "sid1"), null);
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("sid:sid1", a.UnitId);
        Assert.Equal("sid1", a.ClaudeSessionId);
        Assert.False(a.IsAnchor);
    }

    [Fact]
    public void TerminalClaudeExe_IsTerminalAnchor()
    {
        var a = Sut.Attribute(Proc("claude.exe"), Ok(path: TerminalPath), null);
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.StartsWith("term:", a.UnitId);
        Assert.Null(a.ClaudeSessionId);
        Assert.True(a.IsAnchor);
    }

    [Fact]
    public void TerminalClaudeExe_ChildOfSession_InheritsParentUnit_NotAnchor()
    {
        // `claude mcp get ...` run by an agent's shell: no session variables of its own, parent belongs to a session.
        var a = Sut.Attribute(Proc("claude.exe"), Ok(path: TerminalPath), Session("host:local_x", "sid1"));
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "host:local_x", "sid1", false), a);
    }

    [Fact]
    public void TerminalClaudeExe_ChildOfTerminalSession_InheritsThatSession()
    {
        var a = Sut.Attribute(Proc("claude.exe", pid: 200), Ok(path: TerminalPath), Session("term:1@2"));
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "term:1@2", null, false), a);
    }

    [Theory]
    [InlineData("unattributed")]
    [InlineData("desktop-app")]
    public void TerminalClaudeExe_ParentNotASession_IsNewTerminalAnchor(string parentKind)
    {
        var parent = parentKind == "unattributed"
            ? ProcessTracker.Core.Model.Attribution.Unattributed
            : new ProcessTracker.Core.Model.Attribution(AttributionKind.ClaudeApp, null, null, false);
        var a = Sut.Attribute(Proc("claude.exe"), Ok(path: TerminalPath), parent);
        Assert.Equal(new ProcessTracker.Core.Model.Attribution(AttributionKind.Session, "term:100@777", null, true), a);
    }

    [Fact]
    public void DesktopElectron_IsClaudeApp()
    {
        var a = Sut.Attribute(Proc("Claude.exe"), Ok(path: AppPath), null);
        Assert.Equal(AttributionKind.ClaudeApp, a.Kind);
    }

    [Fact]
    public void NoVars_ChildOfClaudeApp_IsClaudeApp()
    {
        var parent = new ProcessTracker.Core.Model.Attribution(AttributionKind.ClaudeApp, null, null, false);
        var a = Sut.Attribute(Proc("helper.exe"), Ok(path: @"C:\tools\helper.exe"), parent);
        Assert.Equal(AttributionKind.ClaudeApp, a.Kind);
        Assert.False(a.IsAnchor);
    }

    [Fact]
    public void NoVars_ChildOfSession_InheritsSession()
    {
        var a = Sut.Attribute(Proc("cmd.exe"), Ok(path: @"C:\Windows\cmd.exe"), Session("host:h", "sid9"));
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("host:h", a.UnitId);
        Assert.Equal("sid9", a.ClaudeSessionId);
        Assert.False(a.IsAnchor);
    }

    [Fact]
    public void AccessDenied_WithSessionParent_Inherits()
    {
        var a = Sut.Attribute(Proc("svc.exe"), Failed(InspectStatus.AccessDenied), Session("host:h", "sid9"));
        Assert.Equal(AttributionKind.Session, a.Kind);
        Assert.Equal("host:h", a.UnitId);
    }

    [Fact]
    public void AccessDenied_NoParent_Unattributed()
    {
        var a = Sut.Attribute(Proc("svc.exe"), Failed(InspectStatus.AccessDenied), null);
        Assert.Equal(ProcessTracker.Core.Model.Attribution.Unattributed, a);
    }

    [Fact]
    public void PlainProcess_Unattributed()
    {
        var a = Sut.Attribute(Proc("notepad.exe"), Ok(path: @"C:\Windows\notepad.exe"),
            ProcessTracker.Core.Model.Attribution.Unattributed);
        Assert.Equal(AttributionKind.Unattributed, a.Kind);
        Assert.Null(a.UnitId);
        Assert.False(a.IsAnchor);
    }
}
