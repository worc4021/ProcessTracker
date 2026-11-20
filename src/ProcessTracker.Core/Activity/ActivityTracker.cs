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

    /// <summary>
    /// <see cref="EffectiveLastActive"/> for every live process in one post-order pass over the tree (each process is
    /// visited once). Terminates on a parent cycle, which cannot occur in practice since a parent is never created
    /// after its child.
    /// </summary>
    public Dictionary<ProcessKey, DateTime?> EffectiveLastActiveAll(ProcessTable t)
    {
        var result = new Dictionary<ProcessKey, DateTime?>(t.Live.Count);
        var onPath = new HashSet<ProcessKey>();
        var stack = new Stack<(ProcessKey Key, bool ChildrenDone)>();
        foreach (var root in t.Live)
        {
            if (result.ContainsKey(root)) continue;
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (k, childrenDone) = stack.Pop();
                if (!childrenDone)
                {
                    if (result.ContainsKey(k) || !onPath.Add(k)) continue;
                    stack.Push((k, true));
                    var children = t.Children(k);
                    for (var i = 0; i < children.Count; i++)
                        if (!result.ContainsKey(children[i]) && !onPath.Contains(children[i])) stack.Push((children[i], false));
                    continue;
                }
                var best = Get(k).LastActiveAt;
                var done = t.Children(k);
                for (var i = 0; i < done.Count; i++)
                    if (result.TryGetValue(done[i], out var ce)) best = ActivityState.Max(best, ce);
                result[k] = best;
                onPath.Remove(k);
            }
        }
        return result;
    }
}
