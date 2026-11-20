using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Orphans;

public static class OrphanClassifier
{
    public static ProcessState Classify(ProcessRecord r, SessionRegistry s, ProcessTable t)
    {
        var a = r.Attribution;
        if (a.Kind != AttributionKind.Session || a.UnitId is not { } unit) return ProcessState.Normal;
        if (!s.IsLive(unit)) return ProcessState.Orphan;
        if (a.IsAnchor) return ProcessState.Normal;
        return r.Parent is { } p && t.IsLive(p) ? ProcessState.Normal : ProcessState.Detached;
    }

    public static bool IsAlertable(ProcessRecord r, ProcessState st, DateTime? effectiveLastActive, DateTime now, TimeSpan idle)
    {
        if (st != ProcessState.Orphan || !IsWatched(r.ImageName)) return false;
        var since = effectiveLastActive ?? r.FirstSeenUtc;
        return now - since >= idle;
    }

    private static bool IsWatched(string image) =>
        image.Equals("MATLAB.exe", StringComparison.OrdinalIgnoreCase)
        || image.Equals("Dymola.exe", StringComparison.OrdinalIgnoreCase)
        || (image.StartsWith("dymosim", StringComparison.OrdinalIgnoreCase)
            && image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
}
