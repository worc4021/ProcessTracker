using System.Text.RegularExpressions;

namespace ProcessTracker.Core.Hooks;

/// <summary>
/// Decides whether a Claude Code tool call starts heavy local work (a build, test suite, simulation, parallel jobs or a
/// subagent). Runs before every Bash, PowerShell, Task and Agent call on agent-supplied text, so every pattern is
/// case-insensitive, culture-invariant and bounded by <see cref="MatchTimeout"/>; a timeout counts as not heavy.
/// </summary>
public static class HeavyCommandClassifier
{
    /// <summary>The longest one pattern may take on one command.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // `make` is a whole word not followed by a hyphen, so `make-dir` and the like are not builds (`mingw32-make` is).
    private static readonly Regex[] BuiltIn =
    [
        .. new[]
        {
            @"cmake --build",
            @"msbuild",
            @"dotnet (build|test|publish)",
            @"\bmake\b(?!-)",
            @"ninja",
            @"cargo (build|test)",
            @"(npm|pnpm|yarn) (run )?(build|test)",
            @"\bpython(3)?(\.exe)?\s+\S+\.py",
            @" -j\s*\d*",
            @" -m:\d+",
            @"--parallel",
            @"Start-Job",
            @"ForEach-Object\s+-Parallel",
        }.Select(p => new Regex(p, Options, MatchTimeout)),
    ];

    /// <summary>A pattern with the classifier's options and timeout (for the extra patterns in configuration).</summary>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static Regex CreatePattern(string pattern) => new(pattern, Options, MatchTimeout);

    /// <summary>
    /// True for the Task and Agent tools, and for a Bash or PowerShell <paramref name="command"/> that matches a built-in
    /// or an <paramref name="extra"/> pattern. Every other tool is not heavy.
    /// </summary>
    public static bool IsHeavy(string toolName, string? command, IReadOnlyList<Regex> extra)
    {
        switch (toolName)
        {
            case "Task" or "Agent":
                return true;
            case "Bash" or "PowerShell" when !string.IsNullOrEmpty(command):
                return BuiltIn.Any(r => Matches(r, command)) || extra.Any(r => Matches(r, command));
            default:
                return false;
        }
    }

    private static bool Matches(Regex r, string command)
    {
        try { return r.IsMatch(command); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
