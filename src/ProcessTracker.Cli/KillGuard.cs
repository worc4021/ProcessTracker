using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Cli;

public sealed record KillDecision(bool Allowed, string? Reason)
{
    public static KillDecision Allow { get; } = new(true, null);
}

/// <summary>
/// Decides whether the caller may kill a target. A caller running inside a Claude Code session may only touch
/// processes of its own session unit; every other caller (Claude desktop app, a terminal outside Claude) is the
/// user acting directly and is allowed.
/// </summary>
public static class KillGuard
{
    public static KillDecision Check(
        Attribution? caller, ProcessViewDto target, bool tree, IReadOnlyList<ProcessViewDto> treeMembers)
    {
        if (caller is not { Kind: AttributionKind.Session }) return KillDecision.Allow;

        // Fail closed: a session caller without a unit id matches nothing.
        var unit = caller.UnitId;
        var members = tree ? treeMembers : [];
        foreach (var p in members.Prepend(target))
        {
            if (unit is null || p.Attribution.UnitId != unit)
                return new KillDecision(false,
                    $"Refused: process {p.Pid} ({p.ImageName}) does not belong to this Claude session. " +
                    "A session may only kill its own processes. Use the tray app or a terminal outside Claude to kill other processes.");
        }
        return KillDecision.Allow;
    }
}
