using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tests.Sessions;

public class SessionInfoTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SessionInfo Info(string? cwd = null, string? title = null, DesktopSessionInfo? desktop = null) =>
        new("host:local_x", "local_x", [], null, T0, null, cwd, title, null, desktop);

    private static DesktopSessionInfo Desk(string? title = null, string? cwd = null, string? origin = null, string? branch = null) =>
        new(title, branch, null, cwd, origin, null, null);

    [Fact]
    public void Title_DesktopFirst()
    {
        var s = Info(@"C:\repos\X", "first prompt", Desk("Sidebar name", origin: @"C:\repos\X"));
        Assert.Equal("Sidebar name", s.DisplayTitle);
        Assert.Equal(TitleSources.Desktop, s.TitleSource);
    }

    [Fact]
    public void Title_TranscriptWhenNoDesktopTitle()
    {
        var s = Info(@"C:\repos\X", "first prompt", Desk(title: "  "));
        Assert.Equal("first prompt", s.DisplayTitle);
        Assert.Equal(TitleSources.Transcript, s.TitleSource);
    }

    [Fact]
    public void Title_FolderWhenNoTitles()
    {
        var s = Info(@"C:\repos\Widget\");
        Assert.Equal("Widget", s.DisplayTitle);
        Assert.Equal(TitleSources.Folder, s.TitleSource);
    }

    [Fact]
    public void Title_UnitIdWhenNothingKnown()
    {
        var s = Info();
        Assert.Equal("host:local_x", s.DisplayTitle);
        Assert.Equal(TitleSources.Id, s.TitleSource);
        Assert.Null(s.Project);
    }

    [Fact]
    public void Project_PrefersOriginCwd_ThenDesktopCwd_ThenTranscriptCwd()
    {
        Assert.Equal("Repo", Info(@"C:\t\other", desktop: Desk(cwd: @"C:\Repo\.claude\worktrees\wt", origin: @"C:\Repo")).Project);
        Assert.Equal("wt", Info(@"C:\t\other", desktop: Desk(cwd: @"C:\Repo\.claude\worktrees\wt")).Project);
        Assert.Equal("other", Info(@"C:\t\other", desktop: Desk()).Project);
    }

    [Fact]
    public void Branch_Summary_EffectiveCwd_FromDesktop()
    {
        var s = Info(@"C:\first", desktop: new DesktopSessionInfo("t", "main", "wt", @"C:\now", null, "did things", null));
        Assert.Equal("main", s.Branch);
        Assert.Equal("did things", s.Summary);
        Assert.Equal(@"C:\now", s.EffectiveCwd);
        Assert.Equal(@"C:\first", Info(@"C:\first").EffectiveCwd);
    }

    [Theory]
    [InlineData(@"C:\repos\X", "X")]
    [InlineData(@"C:\repos\X\", "X")]
    [InlineData("C:/repos/Y/", "Y")]
    [InlineData(@"C:\", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LeafName_Cases(string? path, string? expected) => Assert.Equal(expected, SessionInfo.LeafName(path));
}
