using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests.Fakes;

/// <summary>Scripted inspection results per process key, with a call counter. Unscripted keys inspect as Ok/empty.</summary>
public sealed class FakeInspector : IProcessInspector
{
    private readonly Dictionary<ProcessKey, Queue<InspectResult>> _scripted = new();
    private readonly Dictionary<ProcessKey, int> _calls = new();
    private readonly HashSet<ProcessKey> _throws = new();

    public static InspectResult Ok(string? sessionId = null, string? hostSessionId = null, string? imagePath = null) =>
        new(InspectStatus.Ok, sessionId, hostSessionId, null, imagePath, null);

    public static InspectResult Status(InspectStatus status) => new(status, null, null, null, null, null);

    /// <summary>Results returned in order; the last one repeats.</summary>
    public void Set(ProcessKey key, params InspectResult[] results) => _scripted[key] = new Queue<InspectResult>(results);

    /// <summary>Makes every inspection of <paramref name="key"/> throw.</summary>
    public void Throw(ProcessKey key) => _throws.Add(key);

    public int Calls(ProcessKey key) => _calls.GetValueOrDefault(key);

    public InspectResult Inspect(ProcessKey key)
    {
        _calls[key] = Calls(key) + 1;
        if (_throws.Contains(key)) throw new InvalidOperationException("inspection failed for " + key);
        if (!_scripted.TryGetValue(key, out var q)) return Ok();
        return q.Count > 1 ? q.Dequeue() : q.Peek();
    }
}
