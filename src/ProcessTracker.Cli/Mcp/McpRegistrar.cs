using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Mcp;

public enum McpResult { Done, AlreadyInState, Skipped }

/// <summary>Registers or removes the process-tracker MCP server in the user's Claude Code config (user scope).</summary>
public sealed class McpRegistrar(Func<string?> locateClaude, IProcessRunner runner)
{
    public const string ServerName = "process-tracker";
    public const string Url = "http://127.0.0.1:47811/mcp";
    // Passed literally (no shell): Claude Code expands the variable per session; ":-" keeps terminal sessions valid.
    public const string Header = "X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>claude on the PATH, else %USERPROFILE%\.local\bin\claude.exe; null when neither exists.</summary>
    public static string? LocateClaude()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(profile, ".local", "bin", "claude.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    public McpResult Register()
    {
        if (locateClaude() is not { } claude) return McpResult.Skipped;
        if (InUserScope(claude)) return McpResult.AlreadyInState; // an entry in another scope does not count: still add ours
        var (code, output) = runner.Run(claude,
            ["mcp", "add", "--transport", "http", "--scope", "user", ServerName, Url, "--header", Header], Timeout);
        if (code != 0) throw new InvalidOperationException($"claude mcp add failed with exit code {code}: {output.Trim()}");
        return McpResult.Done;
    }

    public McpResult Unregister()
    {
        if (locateClaude() is not { } claude) return McpResult.Skipped;
        if (!InUserScope(claude)) return McpResult.AlreadyInState;
        var (code, output) = runner.Run(claude, ["mcp", "remove", "--scope", "user", ServerName], Timeout);
        if (code != 0) throw new InvalidOperationException($"claude mcp remove failed with exit code {code}: {output.Trim()}");
        return McpResult.Done;
    }

    /// <summary>True when `claude mcp get` finds the server and (as far as its output says) in user scope.</summary>
    private bool InUserScope(string claude)
    {
        var (code, output) = runner.Run(claude, ["mcp", "get", ServerName], Timeout);
        if (code != 0) return false;
        var m = System.Text.RegularExpressions.Regex.Match(output, @"^\s*Scope:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return !m.Success || m.Groups[1].Value.Equals("User", StringComparison.OrdinalIgnoreCase); // no scope line: assume user
    }
}
