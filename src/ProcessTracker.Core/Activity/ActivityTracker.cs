using ProcessTracker.Core.Model;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Activity;

/// <summary>Records last-activity timestamps per process and rolls them up the process tree.</summary>
public sealed class ActivityTracker(ActivityOptions options)
{
    private readonly Dictionary<ProcessKey, ActivityState> _states = new();

    public void Update(in ProcessSample s, string imageName)
    {
        var t = options.PerImage.TryGetValue(imageName, out var perImage) ? perImage : options.Default;
        var at = s.TimestampUtc;
        var cur = Get(s.Key);

        var next = new ActivityState(
            s.CpuCores > t.CpuCores ? at : cur.LastCpuAt,
            s.ReadOpsDelta + s.OtherOpsDelta >= t.IoOps ? at : cur.LastIoAt,
            s.WriteOpsDelta >= t.WriteOps ? at : cur.LastWriteAt,
            s.ContextSwitchesDelta > t.ContextSwitches ? at : cur.LastWakeAt);

        if (next != cur) _states[s.Key] = next;
    }

    public ActivityState Get(ProcessKey k) => _states.TryGetValue(k, out var st) ? st : default;

    public void Seed(ProcessKey k, ActivityState s) => _states[k] = s;

    public void Forget(ProcessKey k) => _states.Remove(k);

    /// <summary>Latest activity of the process or any live descendant; null if none recorded.</summary>
    public DateTime? EffectiveLastActive(ProcessKey k, ProcessTable t)
    {
        var best = Get(k).LastActiveAt;
        foreach (var d in t.Descendants(k))
            best = ActivityState.Max(best, Get(d).LastActiveAt);
        return best;
    }
}
