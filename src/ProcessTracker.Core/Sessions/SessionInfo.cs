using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Sessions;

/// <summary>Where a session's display title came from, best first.</summary>
public static class TitleSources
{
    /// <summary>The Claude desktop app's sidebar title.</summary>
    public const string Desktop = "desktop";

    /// <summary>The first prompt in the transcript.</summary>
    public const string Transcript = "transcript";

    /// <summary>The project folder name.</summary>
    public const string Folder = "folder";

    /// <summary>The unit id itself (nothing better is known).</summary>
    public const string Id = "id";
}

/// <summary>
/// A Claude Code session unit: liveness is that of its anchor claude.exe. <see cref="Title"/> and <see cref="Cwd"/>
/// come from the transcript, <see cref="Desktop"/> from the desktop app's session metadata (desktop sessions only).
/// </summary>
public sealed record SessionInfo(
    string UnitId,
    string? HostSessionId,
    IReadOnlyList<string> ClaudeSessionIds,
    ProcessKey? Anchor,
    DateTime FirstSeenUtc,
    DateTime? EndedUtc,
    string? Cwd,
    string? Title,
    DateTime? LastTranscriptWriteUtc,
    DesktopSessionInfo? Desktop = null)
{
    /// <summary>The working directory: the desktop app's current one, else the transcript's first.</summary>
    public string? EffectiveCwd => Desktop?.Cwd ?? Cwd;

    /// <summary>Leaf folder name of the desktop origin folder (the repository of a worktree session), else of the cwd.</summary>
    public string? Project => LeafName(Desktop?.OriginCwd) ?? LeafName(Desktop?.Cwd) ?? LeafName(Cwd);

    /// <summary>The git branch the desktop app reports, if any.</summary>
    public string? Branch => Desktop?.Branch;

    /// <summary>The desktop app's summary of the last turn, if any.</summary>
    public string? Summary => Desktop?.Summary;

    /// <summary>Desktop title, else the transcript's first prompt, else the project folder, else the unit id.</summary>
    public string DisplayTitle => ResolveTitle().Title;

    /// <summary>One of <see cref="TitleSources"/>: which source <see cref="DisplayTitle"/> came from.</summary>
    public string TitleSource => ResolveTitle().Source;

    private (string Title, string Source) ResolveTitle()
    {
        if (!string.IsNullOrWhiteSpace(Desktop?.Title)) return (Desktop.Title, TitleSources.Desktop);
        if (!string.IsNullOrWhiteSpace(Title)) return (Title, TitleSources.Transcript);
        if (Project is { } p) return (p, TitleSources.Folder);
        return (UnitId, TitleSources.Id);
    }

    /// <summary>The last path segment (<c>C:\repos\X\</c> gives <c>X</c>); null for a root or an empty path.</summary>
    public static string? LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return null;
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        var leaf = i < 0 ? trimmed : trimmed[(i + 1)..];
        return leaf.Length == 0 || leaf.EndsWith(':') ? null : leaf;
    }
}
