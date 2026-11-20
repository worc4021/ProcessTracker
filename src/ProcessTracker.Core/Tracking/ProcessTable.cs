using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tracking;

/// <summary>The live process set with parent/child links. Single-threaded use.</summary>
public sealed class ProcessTable
{
    private readonly Dictionary<ProcessKey, ProcessKey?> _parents = new();
    private readonly Dictionary<ProcessKey, List<ProcessKey>> _children = new();

    /// <summary>Replaces the whole live set (the dictionaries are reused from tick to tick).</summary>
    public void SetLive(IEnumerable<(ProcessKey Key, ProcessKey? Parent)> live)
    {
        _parents.Clear();
        _children.Clear();
        foreach (var (key, parent) in live)
        {
            _parents[key] = parent;
            if (parent is { } p)
            {
                if (!_children.TryGetValue(p, out var list)) _children[p] = list = new List<ProcessKey>(2);
                list.Add(key);
            }
        }
    }

    public IReadOnlyList<ProcessKey> Children(ProcessKey k) =>
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

    /// <summary>Every live process.</summary>
    public IReadOnlyCollection<ProcessKey> Live => _parents.Keys;

    public ProcessKey? ParentOf(ProcessKey k) => _parents.TryGetValue(k, out var p) ? p : null;
}
