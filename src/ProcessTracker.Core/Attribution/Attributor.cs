using ProcessTracker.Core.Model;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Attribution;

/// <summary>Ordered rules (first match wins) deciding which Claude session, if any, owns a process.</summary>
public sealed class Attributor
{
    private const string ClaudeImage = "claude.exe";
    private const string DesktopAppMarker = @"\WindowsApps\Claude_";

    public Attr Attribute(RawProcessCounters proc, InspectResult inspect, Attr? parent)
    {
        if (inspect.Status == InspectStatus.Ok)
        {
            var isClaudeExe = string.Equals(proc.ImageName, ClaudeImage, StringComparison.OrdinalIgnoreCase);
            var isDesktopApp = inspect.ImagePath?.Contains(DesktopAppMarker, StringComparison.OrdinalIgnoreCase) == true;

            // 1. Host session id. A present-but-empty CLAUDE_CODE_SESSION_ID counts as absent. A claude.exe without a
            // session id is the anchor unless its parent already belongs to the same unit: a `claude ...` run from
            // inside the session (e.g. `claude mcp get`) inherits the host id but is just another session process.
            var sessionId = string.IsNullOrEmpty(inspect.SessionId) ? null : inspect.SessionId;
            if (!string.IsNullOrEmpty(inspect.HostSessionId))
            {
                var unit = "host:" + inspect.HostSessionId;
                var parentInUnit = parent is { Kind: AttributionKind.Session } && parent.UnitId == unit;
                return new Attr(AttributionKind.Session, unit, sessionId, isClaudeExe && sessionId is null && !parentInUnit);
            }

            // 2. Session id only (terminal child).
            if (sessionId is not null)
                return parent is { Kind: AttributionKind.Session }
                    ? new Attr(AttributionKind.Session, parent.UnitId, sessionId, false)
                    : new Attr(AttributionKind.Session, "sid:" + sessionId, sessionId, false);

            // 3. Terminal claude.exe (not the desktop app's copy).
            if (isClaudeExe && inspect.ImagePath is not null && !isDesktopApp)
                return new Attr(AttributionKind.Session, $"term:{proc.Key}", null, true);

            // 4. Desktop app.
            if (isDesktopApp)
                return new Attr(AttributionKind.ClaudeApp, null, null, false);
        }

        // 5. Inherit from an attributed parent.
        if (parent is not null && parent.Kind != AttributionKind.Unattributed)
            return new Attr(parent.Kind, parent.UnitId, parent.ClaudeSessionId, false);

        // 6. Nobody.
        return Attr.Unattributed;
    }
}
