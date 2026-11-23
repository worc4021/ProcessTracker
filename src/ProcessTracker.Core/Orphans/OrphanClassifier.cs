using System.IO.Enumeration;
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

    /// <summary>
    /// True for an orphan idle for at least <paramref name="idle"/> whose image is watched. With a null or empty
    /// <paramref name="watchedImages"/> every image is watched; otherwise only images matching one of its patterns
    /// (<c>*</c> and <c>?</c> wildcards, case-insensitive).
    /// </summary>
    public static bool IsAlertable(ProcessRecord r, ProcessState st, DateTime? effectiveLastActive, DateTime now, TimeSpan idle,
        IReadOnlyList<string>? watchedImages = null)
    {
        if (st != ProcessState.Orphan || !IsWatched(r.ImageName, watchedImages)) return false;
        var since = effectiveLastActive ?? r.FirstSeenUtc;
        return now - since >= idle;
    }

    private static bool IsWatched(string image, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return true;
        foreach (var p in patterns)
            if (FileSystemName.MatchesSimpleExpression(p, image, ignoreCase: true)) return true;
        return false;
    }
}
