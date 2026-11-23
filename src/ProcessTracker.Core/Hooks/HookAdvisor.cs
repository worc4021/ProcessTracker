using System.Globalization;
using System.Text;
using ProcessTracker.Core.Load;

namespace ProcessTracker.Core.Hooks;

/// <summary>
/// The text the Claude Code hooks add to a session's context. It only ever informs: the session decides, and no hook
/// response blocks a tool call.
/// </summary>
public static class HookAdvisor
{
    public const string DecideLine =
        "Decide based on what the user has told you about using this machine (foreground work vs a remote or dedicated box). "
        + "If you don't know and the machine is at Critical, ask them.";

    public const string RuleLine =
        "Before heavy local work, check get_machine_load and pace to suggestedParallelism; "
        + "the ProcessTracker hook warns you at Moderate and above.";

    private const int SessionStartTopSessions = 3;

    /// <summary>
    /// The warning before a tool call, or null to say nothing (Low pressure, or not heavy work). Moderate gets one line;
    /// High and Critical get the full block when <paramref name="full"/>, else a one-line reminder. The caller's own
    /// session is marked in the list: by <see cref="LoadReport.Caller"/> when known, else by <paramref name="callerTitle"/>.
    /// </summary>
    public static string? PreToolUse(LoadReport load, bool heavy, bool full, TimeSpan? sinceInput, string? callerTitle)
    {
        if (!heavy || load.Pressure == Pressure.Low) return null;
        if (load.Pressure == Pressure.Moderate)
            return Invariant($"ProcessTracker: machine at Moderate; run at most {load.SuggestedParallelism} heavy tasks in parallel.");
        if (!full)
            return Invariant($"ProcessTracker: machine still at {load.Pressure}; suggested parallelism {load.SuggestedParallelism}.");

        var text = new StringBuilder()
            .Append(Invariant($"ProcessTracker: machine at {load.Pressure}: CPU {load.CpuBusyPercent:0}% busy, "))
            .Append(Invariant($"{load.CommitHeadroomGb:0.0} GB commit headroom, suggested parallelism {load.SuggestedParallelism}.\n"));
        if (load.TopSessions.Count > 0)
        {
            text.Append("Top sessions:\n");
            foreach (var s in load.TopSessions)
            {
                var mine = load.Caller is { } c ? s.UnitId == c.UnitId : callerTitle is not null && s.Title == callerTitle;
                text.Append(Invariant($"- {TitleOf(s)}: {Usage(s)}")).Append(mine ? " (this session)\n" : "\n");
            }
        }
        return text.Append(LastInput(sinceInput)).Append('\n').Append(DecideLine).ToString();
    }

    /// <summary>
    /// The context paragraph a session gets when it starts: mode, pressure and parallelism, the top three sessions, the
    /// user's last input and the load-check rule; then a version-skew line when the plugin and the tracker differ (an older
    /// tracker is pointed out in user and service mode, each with its own way to update; a dev build gets no line).
    /// </summary>
    public static string SessionStart(LoadReport load, string mode, TimeSpan? sinceInput, Version? pluginVersion, Version trackerVersion)
    {
        var top = load.TopSessions.Take(SessionStartTopSessions).Select(s => Invariant($"{TitleOf(s)} ({Usage(s)})")).ToList();
        var text = new StringBuilder()
            .Append(Invariant($"ProcessTracker ({mode} mode): machine at {load.Pressure}, suggested parallelism {load.SuggestedParallelism}.\n"))
            .Append("Top sessions: ").Append(top.Count > 0 ? string.Join("; ", top) : "none").Append(".\n")
            .Append(LastInput(sinceInput)).Append('\n')
            .Append(RuleLine);

        if (pluginVersion is not null && trackerVersion < pluginVersion)
        {
            // Only user mode is updated by /process-tracker:install; a dev build is whatever the developer built.
            if (mode == "user")
            {
                text.Append(Invariant($"\nProcessTracker {trackerVersion} is older than the plugin ({pluginVersion}); "))
                    .Append("it updates itself daily, or the user can run /process-tracker:install now.");
            }
            else if (mode == "service")
            {
                text.Append(Invariant($"\nProcessTracker {trackerVersion} is older than the plugin ({pluginVersion}); "))
                    .Append("the machine-wide service updates itself daily (winget or GitHub).");
            }
        }
        else if (pluginVersion is not null && pluginVersion < trackerVersion)
        {
            text.Append(Invariant($"\nThe process-tracker plugin ({pluginVersion}) is older than the tracker ({trackerVersion}); "))
                .Append("suggest the user enable auto-update for the processtracker marketplace in /plugin.");
        }
        return text.ToString();
    }

    /// <summary>An age in its largest whole unit: "4 s", "12 min", "3 h", "2 d" (hours up to 47).</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 60 ? Invariant($"{(int)age.TotalSeconds} s")
            : age.TotalMinutes < 60 ? Invariant($"{(int)age.TotalMinutes} min")
            : age.TotalHours < 48 ? Invariant($"{(int)age.TotalHours} h")
            : Invariant($"{(int)age.TotalDays} d");
    }

    private static string LastInput(TimeSpan? sinceInput) =>
        sinceInput is { } age ? $"User last active {FormatAge(age)} ago." : "User activity unknown.";

    private static string TitleOf(SessionUsage s) => string.IsNullOrWhiteSpace(s.Title) ? s.UnitId : s.Title;

    private static string Usage(SessionUsage s) => Invariant($"{s.CpuCores:0.0} cores, {s.PrivateGb:0.0} GB");

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
