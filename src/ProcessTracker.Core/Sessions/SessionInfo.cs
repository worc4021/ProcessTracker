using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Sessions;

/// <summary>A Claude Code session unit: liveness is that of its anchor claude.exe.</summary>
public sealed record SessionInfo(
    string UnitId,
    string? HostSessionId,
    IReadOnlyList<string> ClaudeSessionIds,
    ProcessKey? Anchor,
    DateTime FirstSeenUtc,
    DateTime? EndedUtc,
    string? Cwd,
    string? Title,
    DateTime? LastTranscriptWriteUtc);
