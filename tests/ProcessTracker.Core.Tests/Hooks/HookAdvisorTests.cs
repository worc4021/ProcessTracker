using ProcessTracker.Core.Hooks;
using ProcessTracker.Core.Load;

namespace ProcessTracker.Core.Tests.Hooks;

public sealed class HookAdvisorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string DecideLine =
        "Decide based on what the user has told you about using this machine (foreground work vs a remote or dedicated box). "
        + "If you don't know and the machine is at Critical, ask them.";

    private const string RuleLine =
        "Before heavy local work, check get_machine_load and pace to suggestedParallelism; "
        + "the ProcessTracker hook warns you at Moderate and above.";

    private static readonly SessionUsage Build = new("host:a", "Build the world", @"C:\a", 6f, 12.3, 40);
    private static readonly SessionUsage Docs = new("host:b", "Write docs", @"C:\b", 0.5f, 1.5, 3);
    private static readonly SessionUsage Sim = new("host:c", null, @"C:\c", 2f, 4, 5);
    private static readonly SessionUsage Idle = new("host:d", "Idle", @"C:\d", 0f, 0.5, 1);

    private static LoadReport Load(Pressure p, int parallelism = 1, SessionUsage? caller = null, params SessionUsage[] top) =>
        new(T0, p, 95.4f, 0.7f, 3.0, 1.46, parallelism, caller, top);

    [Theory]
    [InlineData(Pressure.Low, true)]
    [InlineData(Pressure.Critical, false)]
    [InlineData(Pressure.Moderate, false)]
    public void PreToolUse_LowOrNotHeavy_ReturnsNull(Pressure p, bool heavy) =>
        Assert.Null(HookAdvisor.PreToolUse(Load(p), heavy, full: true, TimeSpan.FromSeconds(4), null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreToolUse_Moderate_OneLine(bool full) =>
        Assert.Equal(
            "ProcessTracker: machine at Moderate; run at most 3 heavy tasks in parallel.",
            HookAdvisor.PreToolUse(Load(Pressure.Moderate, 3), heavy: true, full, TimeSpan.FromSeconds(4), null));

    [Fact]
    public void PreToolUse_Critical_FullBlockEndsWithDecideLine()
    {
        var text = HookAdvisor.PreToolUse(Load(Pressure.Critical, 1, null, Build, Sim), heavy: true, full: true,
            TimeSpan.FromSeconds(4), null);

        Assert.NotNull(text);
        Assert.Equal(
            "ProcessTracker: machine at Critical: CPU 95% busy, 1.5 GB commit headroom, suggested parallelism 1.\n"
            + "Top sessions:\n"
            + "- Build the world: 6.0 cores, 12.3 GB\n"
            + "- host:c: 2.0 cores, 4.0 GB\n"
            + "User last active 4 s ago.\n"
            + DecideLine,
            text);
        Assert.EndsWith(DecideLine, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Pressure.High)]
    [InlineData(Pressure.Critical)]
    public void PreToolUse_HighNotFull_ShortLine(Pressure p) =>
        Assert.Equal(
            $"ProcessTracker: machine still at {p}; suggested parallelism 1.",
            HookAdvisor.PreToolUse(Load(p, 1, null, Build), heavy: true, full: false, TimeSpan.FromSeconds(4), null));

    [Fact]
    public void PreToolUse_MarksCallerSession()
    {
        var text = HookAdvisor.PreToolUse(Load(Pressure.High, 1, Docs, Build, Docs), heavy: true, full: true,
            TimeSpan.FromMinutes(12), Docs.Title)!;

        Assert.Contains("- Write docs: 0.5 cores, 1.5 GB (this session)\n", text, StringComparison.Ordinal);
        Assert.Contains("- Build the world: 6.0 cores, 12.3 GB\n", text, StringComparison.Ordinal);
        Assert.Single(text.Split("(this session)")[1..]);
    }

    [Fact]
    public void PreToolUse_UnknownInput_SaysUnknown()
    {
        var text = HookAdvisor.PreToolUse(Load(Pressure.High, 1, null, Build), heavy: true, full: true, null, null)!;

        Assert.Contains("\nUser activity unknown.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("last active", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(4.9, "4 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 min")]
    [InlineData(12 * 60 + 30, "12 min")]
    [InlineData(3 * 3600 + 59 * 60, "3 h")]
    [InlineData(47 * 3600, "47 h")]
    [InlineData(3 * 86400, "3 d")]
    public void FormatAge_IsHumanReadable(double seconds, string expected) =>
        Assert.Equal(expected, HookAdvisor.FormatAge(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void SessionStart_IncludesRuleLine()
    {
        var text = HookAdvisor.SessionStart(Load(Pressure.Low, 4, null, Build, Docs, Sim, Idle), "user",
            TimeSpan.FromMinutes(12), new Version(1, 2, 0), new Version(1, 2, 0));

        Assert.Equal(
            "ProcessTracker (user mode): machine at Low, suggested parallelism 4.\n"
            + "Top sessions: Build the world (6.0 cores, 12.3 GB); Write docs (0.5 cores, 1.5 GB); host:c (2.0 cores, 4.0 GB).\n"
            + "User last active 12 min ago.\n"
            + RuleLine,
            text);
    }

    [Fact]
    public void SessionStart_NoSessions_SaysNone()
    {
        var text = HookAdvisor.SessionStart(Load(Pressure.Moderate, 2), "service", null, null, new Version(1, 2, 0));

        Assert.Contains("Top sessions: none.\n", text, StringComparison.Ordinal);
        Assert.Contains("User activity unknown.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_OlderTracker_AddsUpdateLine()
    {
        var text = HookAdvisor.SessionStart(Load(Pressure.Low, 4), "user", null, new Version(1, 3, 0), new Version(1, 2, 5));

        Assert.EndsWith(
            RuleLine + "\nProcessTracker 1.2.5 is older than the plugin (1.3.0); it updates itself daily, or the user can run "
            + "/process-tracker:install now.",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-update", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_OlderPlugin_SuggestsAutoUpdate()
    {
        var text = HookAdvisor.SessionStart(Load(Pressure.Low, 4), "user", null, new Version(1, 2, 0), new Version(1, 10, 0));

        Assert.EndsWith(
            RuleLine + "\nThe process-tracker plugin (1.2.0) is older than the tracker (1.10.0); suggest the user enable "
            + "auto-update for the processtracker marketplace in /plugin.",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("updates itself daily", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_NoPluginVersion_NoSkewLine()
    {
        var text = HookAdvisor.SessionStart(Load(Pressure.Low, 4), "dev", null, null, new Version(1, 2, 0));

        Assert.EndsWith(RuleLine, text, StringComparison.Ordinal);
    }
}
