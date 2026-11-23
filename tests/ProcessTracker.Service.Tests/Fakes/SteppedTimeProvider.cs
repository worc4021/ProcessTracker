namespace ProcessTracker.Service.Tests.Fakes;

/// <summary>
/// A clock that moves only when the test calls <see cref="Advance"/>, with timers (so <c>Task.Delay(…, time, …)</c>)
/// that fire then, on the calling thread. Nothing depends on real time passing.
/// </summary>
public sealed class SteppedTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<SteppedTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Timers that are armed (a delay somebody is waiting on).</summary>
    public int ArmedTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppedTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var t in due) t.Due = t.Period is { } p ? _now + p : null;
        }
        foreach (var t in due) t.Callback(t.State);
    }

    private sealed class SteppedTimer(SteppedTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
