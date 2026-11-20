using System.Text.Json;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Service.Api;

/// <summary>
/// The serialized <c>/api/overview</c> of the latest <see cref="TrackerView"/>, per normalized set of expanded sessions.
/// A view is immutable and replaced once per tick, so polls within a tick that ask for the same sessions (from any
/// number of open dashboards) share one build and one serialization. At most <see cref="MaxEntries"/> sets are kept
/// per view; everything is dropped when the view changes.
/// </summary>
public sealed class OverviewCache
{
    public const int MaxEntries = 8;

    private readonly object _gate = new();
    private TrackerView? _view;
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public byte[] GetJson(ITrackerHost host, string? expand)
    {
        var view = host.View;
        var expanded = RestEndpoints.ParseExpand(expand, view);
        var key = string.Join(',', expanded);
        lock (_gate)
        {
            if (!ReferenceEquals(view, _view))
            {
                _entries.Clear();
                _view = view;
            }
            if (_entries.TryGetValue(key, out var json)) return json;
            json = JsonSerializer.SerializeToUtf8Bytes(RestEndpoints.Overview(host, view, expanded), ContractJson.Options);
            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = json;
            return json;
        }
    }
}
