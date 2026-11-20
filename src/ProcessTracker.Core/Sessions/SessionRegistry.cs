using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Sessions;

/// <summary>Tracks session units and whether their anchor process is still running. Single-threaded use.</summary>
public sealed class SessionRegistry
{
    private const string HostPrefix = "host:";
    private readonly Dictionary<string, SessionInfo> _sessions = new();

    public void Observe(ProcessRecord r)
    {
        var a = r.Attribution;
        if (a.Kind != AttributionKind.Session || a.UnitId is not { } unit) return;

        if (!_sessions.TryGetValue(unit, out var s))
        {
            var host = unit.StartsWith(HostPrefix, StringComparison.Ordinal) ? unit[HostPrefix.Length..] : null;
            s = new SessionInfo(unit, host, [], null, r.FirstSeenUtc, null, null, null, null);
        }

        var ids = s.ClaudeSessionIds;
        if (a.ClaudeSessionId is { } id && !ids.Contains(id)) ids = [.. ids, id];

        // A live anchor takes over when there is none, when the current one has exited (the session ended, so
        // another running anchor of the unit revives it), or when it is newer (the session was resumed). An older
        // anchor never displaces a running one, and an exited record never replaces anything.
        var takeAnchor = a.IsAnchor && r.ExitedUtc is null
            && (s.Anchor is not { } cur || s.EndedUtc is not null || r.Key.CreateTimeTicks > cur.CreateTimeTicks);

        _sessions[unit] = s with
        {
            ClaudeSessionIds = ids,
            Anchor = takeAnchor ? r.Key : s.Anchor,
            EndedUtc = takeAnchor ? null : s.EndedUtc,
            FirstSeenUtc = r.FirstSeenUtc < s.FirstSeenUtc ? r.FirstSeenUtc : s.FirstSeenUtc,
        };
    }

    public void ProcessExited(ProcessKey k, DateTime now)
    {
        foreach (var s in _sessions.Values.Where(s => s.Anchor == k && s.EndedUtc is null).ToList())
            _sessions[s.UnitId] = s with { EndedUtc = now };
    }

    public bool IsLive(string unitId) =>
        _sessions.TryGetValue(unitId, out var s) && s.Anchor is not null && s.EndedUtc is null;

    public SessionInfo? Get(string unitId) => _sessions.GetValueOrDefault(unitId);

    public IReadOnlyList<SessionInfo> All() => _sessions.Values.ToList();

    public void Restore(IEnumerable<SessionInfo> sessions)
    {
        foreach (var s in sessions) _sessions[s.UnitId] = s;
    }

    public void ApplyTranscript(string unitId, TranscriptInfo t)
    {
        if (!_sessions.TryGetValue(unitId, out var s)) return;
        _sessions[unitId] = s with
        {
            Cwd = t.Cwd ?? s.Cwd,
            Title = t.Title ?? s.Title,
            LastTranscriptWriteUtc = t.LastWriteUtc,
        };
    }

    public void ApplyDesktop(string unitId, DesktopSessionInfo d)
    {
        if (_sessions.TryGetValue(unitId, out var s)) _sessions[unitId] = s with { Desktop = d };
    }
}
