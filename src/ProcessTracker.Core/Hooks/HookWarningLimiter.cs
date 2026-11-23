namespace ProcessTracker.Core.Hooks;

/// <summary>
/// Rations the full High/Critical warning: at most once per <see cref="Interval"/> per session, so a session making
/// many tool calls in a row gets the details once and a one-line reminder after that.
/// </summary>
public sealed class HookWarningLimiter(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    // Past this many keys, expired ones are dropped, so ended sessions do not accumulate.
    private const int PruneAbove = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFull = new(StringComparer.Ordinal);

    /// <summary>True (and the key's interval restarts) when <paramref name="sessionKey"/> had no full warning in the last minute.</summary>
    public bool TakeFull(string sessionKey)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_lastFull.TryGetValue(sessionKey, out var last) && now - last < Interval) return false;
            if (_lastFull.Count >= PruneAbove)
            {
                foreach (var (key, at) in _lastFull)
                {
                    if (now - at >= Interval) _lastFull.Remove(key);
                }
            }
            _lastFull[sessionKey] = now;
            return true;
        }
    }
}
