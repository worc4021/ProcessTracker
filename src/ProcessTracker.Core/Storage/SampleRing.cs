namespace ProcessTracker.Core.Storage;

/// <summary>Fixed-capacity in-memory ring of recent tick frames (300 = 10 min at 2 s). Thread-safe.</summary>
public sealed class SampleRing
{
    private readonly TickFrame?[] _items;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public SampleRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new TickFrame?[capacity];
    }

    public void Add(TickFrame f)
    {
        lock (_gate)
        {
            _items[_next] = f;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    public TickFrame? Latest
    {
        get
        {
            lock (_gate)
                return _count == 0 ? null : _items[(_next - 1 + _items.Length) % _items.Length];
        }
    }

    /// <summary>Frames with TimestampUtc >= fromUtc, oldest first.</summary>
    public IReadOnlyList<TickFrame> Since(DateTime fromUtc)
    {
        lock (_gate)
        {
            var result = new List<TickFrame>();
            var start = (_next - _count + _items.Length) % _items.Length;
            for (var i = 0; i < _count; i++)
            {
                var f = _items[(start + i) % _items.Length]!;
                if (f.TimestampUtc >= fromUtc) result.Add(f);
            }
            return result;
        }
    }
}
