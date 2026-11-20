using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests.Fakes;

/// <summary>A scripted process list: tests add/remove processes and set the clock and CPU counters.</summary>
public sealed class FakeSnapshotSource : ISnapshotSource
{
    private readonly List<RawProcessCounters> _processes = new();

    public DateTime Now { get; set; }

    public SystemCounters System { get; set; } = new(8, 0, 0, 0, 1_000, 2_000, 3_000, 4_000);

    public int TakeCount { get; private set; }

    public FakeSnapshotSource(DateTime now) => Now = now;

    public ProcessKey Add(int pid, long createTicks, int parentPid, string image, long cpuTicks = 0)
    {
        var key = new ProcessKey(pid, createTicks);
        _processes.RemoveAll(p => p.Key == key);
        _processes.Add(Raw(key, parentPid, image, cpuTicks));
        return key;
    }

    public void Remove(ProcessKey key) => _processes.RemoveAll(p => p.Key == key);

    /// <summary>Sets cumulative CPU time (100 ns units), split evenly between kernel and user.</summary>
    public void SetCpu(ProcessKey key, long cpuTicks)
    {
        var i = _processes.FindIndex(p => p.Key == key);
        var p = _processes[i];
        _processes[i] = p with { KernelTime = cpuTicks / 2, UserTime = cpuTicks - cpuTicks / 2 };
    }

    public void Advance(TimeSpan by) => Now += by;

    public Snapshot Take()
    {
        TakeCount++;
        return new Snapshot(Now, _processes.ToArray(), System);
    }

    private static RawProcessCounters Raw(ProcessKey key, int parentPid, string image, long cpuTicks) =>
        new(key, parentPid, image, cpuTicks / 2, cpuTicks - cpuTicks / 2, 1_000_000, 2_000_000,
            0, 0, 0, 0, 0, 0, 0, 10, 2);
}
