using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tracking;

/// <summary>The live process set with parent/child links.</summary>
public sealed class ProcessTable
{
    private Dictionary<ProcessKey, ProcessKey?> _parents = new();
    private Dictionary<ProcessKey, List<ProcessKey>> _children = new();

    /// <summary>Replaces the whole live set.</summary>
    public void SetLive(IEnumerable<(ProcessKey Key, ProcessKey? Parent)> live)
    {
        var parents = new Dictionary<ProcessKey, ProcessKey?>();
        var children = new Dictionary<ProcessKey, List<ProcessKey>>();
        foreach (var (key, parent) in live)
        {
            parents[key] = parent;
            if (parent is { } p)
            {
                if (!children.TryGetValue(p, out var list)) children[p] = list = new List<ProcessKey>();
                list.Add(key);
            }
        }
        _parents = parents;
        _children = children;
    }

    public IEnumerable<ProcessKey> Children(ProcessKey k) =>
        _children.TryGetValue(k, out var list) ? list : [];

    /// <summary>All transitive live children of <paramref name="k"/>, excluding itself; cycle-safe.</summary>
    public IEnumerable<ProcessKey> Descendants(ProcessKey k)
    {
        var visited = new HashSet<ProcessKey> { k };
        var stack = new Stack<ProcessKey>();
        stack.Push(k);
        while (stack.Count > 0)
            foreach (var c in Children(stack.Pop()))
                if (visited.Add(c))
                {
                    stack.Push(c);
                    yield return c;
                }
    }

    public bool IsLive(ProcessKey k) => _parents.ContainsKey(k);

    public ProcessKey? ParentOf(ProcessKey k) => _parents.TryGetValue(k, out var p) ? p : null;
}
