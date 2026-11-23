using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Tests.Activity;

public class ActivityTrackerTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessKey Key(int pid) => new(pid, 1000 + pid);

    private static ProcessSample Sample(ProcessKey key, DateTime at, float cpu = 0f,
        long readOps = 0, long writeOps = 0, long otherOps = 0, long ctx = 0) =>
        new(key, at, cpu, 0, 0, readOps, writeOps, otherOps, 0, 0, 0, ctx, 0, 0);

    [Fact]
    public void CpuAboveThreshold_SetsLastCpuAt()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.5f), "foo.exe");
        Assert.Equal(T, tracker.Get(k).LastCpuAt);
        Assert.Equal(T, tracker.Get(k).LastActiveAt);
    }

    [Fact]
    public void CpuBelowThreshold_LeavesNull()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.005f), "foo.exe");
        Assert.Null(tracker.Get(k).LastCpuAt);
        Assert.Null(tracker.Get(k).LastActiveAt);
    }

    [Fact]
    public void WriteOps_SetsWriteAndNotIo()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, writeOps: 3), "foo.exe");
        var s = tracker.Get(k);
        Assert.Equal(T, s.LastWriteAt);
        Assert.Null(s.LastIoAt);
    }

    [Fact]
    public void DefaultPerImage_IsEmpty_SoNoImageGetsSpecialThresholds()
    {
        Assert.Empty(new ActivityOptions().PerImage);
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.03f, ctx: 1500), "engine.exe");
        Assert.NotNull(tracker.Get(k).LastActiveAt);
    }

    /// <summary>Per-image thresholds that suppress a JVM-like noise profile for engine.exe only.</summary>
    private static ActivityOptions NoisyEngineOptions() => new()
    {
        PerImage = new Dictionary<string, ActivityThresholds>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine.exe"] = new ActivityThresholds(0.05f, 1, 1, 2000),
        },
    };

    [Fact]
    public void Update_ReportsWhetherAnySignalFired_UsingPerImageThresholds()
    {
        var tracker = new ActivityTracker(NoisyEngineOptions());
        Assert.True(tracker.Update(Sample(Key(1), T, ctx: 150), "foo.exe"));
        Assert.False(tracker.Update(Sample(Key(2), T, ctx: 1, cpu: 0.001f), "foo.exe"));
        Assert.False(tracker.Update(Sample(Key(3), T, cpu: 0.03f, ctx: 1500), "engine.exe"));
        Assert.True(tracker.Update(Sample(Key(4), T, writeOps: 1), "engine.exe"));
        // Still active although nothing changed in the stored state.
        Assert.True(tracker.Update(Sample(Key(1), T, ctx: 150), "foo.exe"));
    }

    [Fact]
    public void ConfiguredNoisyImage_NotActive()
    {
        var tracker = new ActivityTracker(NoisyEngineOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.03f, ctx: 1500), "engine.exe");
        Assert.Null(tracker.Get(k).LastActiveAt);
    }

    [Fact]
    public void ImageLookupIsCaseInsensitive()
    {
        var tracker = new ActivityTracker(NoisyEngineOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.03f, ctx: 1500), "ENGINE.exe");
        Assert.Null(tracker.Get(k).LastActiveAt);
        // Same sample under a default-threshold image does fire.
        var k2 = Key(2);
        tracker.Update(Sample(k2, T, cpu: 0.03f, ctx: 1500), "other.exe");
        Assert.NotNull(tracker.Get(k2).LastActiveAt);
    }

    [Fact]
    public void EffectiveLastActive_TakesBusyGrandchild()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var solver = Key(1); var child = Key(2); var worker = Key(3);
        var table = new ProcessTable();
        table.SetLive([(solver, null), (child, solver), (worker, child)]);

        tracker.Update(Sample(solver, T), "solver.exe");
        tracker.Update(Sample(worker, T.AddSeconds(5), cpu: 1f), "worker.exe");

        Assert.Equal(T.AddSeconds(5), tracker.EffectiveLastActive(solver, table));
    }

    [Fact]
    public void EffectiveLastActive_IgnoresDeadDescendants()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var parent = Key(1); var child = Key(2);
        var table = new ProcessTable();
        table.SetLive([(parent, null), (child, parent)]);
        tracker.Update(Sample(parent, T, cpu: 1f), "solver.exe");
        tracker.Update(Sample(child, T.AddSeconds(5), cpu: 1f), "worker.exe");

        table.SetLive([(parent, null)]);

        Assert.Equal(T, tracker.EffectiveLastActive(parent, table));
    }

    [Fact]
    public void EffectiveLastActive_NoActivity_ReturnsNull()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var table = new ProcessTable();
        table.SetLive([(Key(1), null)]);
        Assert.Null(tracker.EffectiveLastActive(Key(1), table));
    }

    [Fact]
    public void EffectiveLastActiveAll_MatchesPerProcessRollUp()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var root = Key(1); var a = Key(2); var a1 = Key(3); var a2 = Key(4); var b = Key(5); var lone = Key(6);
        var table = new ProcessTable();
        table.SetLive([(a1, a), (root, null), (a, root), (a2, a), (b, root), (lone, Key(99))]);
        tracker.Update(Sample(root, T, cpu: 1f), "x.exe");
        tracker.Update(Sample(a1, T.AddSeconds(3), cpu: 1f), "x.exe");
        tracker.Update(Sample(b, T.AddSeconds(7), cpu: 1f), "x.exe");

        var all = tracker.EffectiveLastActiveAll(table);

        Assert.Equal(6, all.Count);
        foreach (var k in new[] { root, a, a1, a2, b, lone })
            Assert.Equal(tracker.EffectiveLastActive(k, table), all[k]);
        Assert.Equal(T.AddSeconds(7), all[root]);
        Assert.Equal(T.AddSeconds(3), all[a]);
        Assert.Null(all[a2]);
    }

    [Fact]
    public void EffectiveLastActiveAll_TerminatesOnParentCycle()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var a = Key(1); var b = Key(2);
        var table = new ProcessTable();
        table.SetLive([(a, b), (b, a)]);
        tracker.Update(Sample(b, T, cpu: 1f), "x.exe");

        var all = tracker.EffectiveLastActiveAll(table);

        Assert.Equal(2, all.Count);
        Assert.Equal(T, all[b]);
    }

    [Fact]
    public void Descendants_SurvivesParentCycle()
    {
        var a = Key(1); var b = Key(2);
        var table = new ProcessTable();
        table.SetLive([(a, b), (b, a)]);
        var d = table.Descendants(a).ToList();
        Assert.Equal([b], d);
    }
}
