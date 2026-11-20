using System.Text.Json;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Service.Api;

/// <summary>
/// The serialized <c>/api/overview</c> of the latest <see cref="TrackerView"/>. A view is immutable and replaced once
/// per tick, so every poll (from any number of open dashboards) within a tick reuses one build and one serialization.
/// </summary>
public sealed class OverviewCache
{
    private readonly object _gate = new();
    private TrackerView? _view;
    private byte[] _json = [];

    public byte[] GetJson(ITrackerHost host)
    {
        var view = host.View;
        lock (_gate)
        {
            if (!ReferenceEquals(view, _view))
            {
                _json = JsonSerializer.SerializeToUtf8Bytes(RestEndpoints.Overview(host, view), ContractJson.Options);
                _view = view;
            }
            return _json;
        }
    }
}
