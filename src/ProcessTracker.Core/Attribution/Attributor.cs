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

            // 1. Host session id.
            if (!string.IsNullOrEmpty(inspect.HostSessionId))
                return new Attr(AttributionKind.Session, "host:" + inspect.HostSessionId, inspect.SessionId,
                    isClaudeExe && inspect.SessionId == null);

            // 2. Session id only (terminal child).
            if (!string.IsNullOrEmpty(inspect.SessionId))
                return parent is { Kind: AttributionKind.Session }
                    ? new Attr(AttributionKind.Session, parent.UnitId, inspect.SessionId, false)
                    : new Attr(AttributionKind.Session, "sid:" + inspect.SessionId, inspect.SessionId, false);

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
