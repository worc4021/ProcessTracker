namespace ProcessTracker.Core.Model;

public enum AttributionKind { Session, ClaudeApp, Unattributed }

/// <summary>Who a process belongs to: a Claude Code session unit, the Claude desktop app, or nobody.</summary>
public sealed record Attribution(AttributionKind Kind, string? UnitId, string? ClaudeSessionId, bool IsAnchor)
{
    public static Attribution Unattributed { get; } = new(AttributionKind.Unattributed, null, null, false);
}
