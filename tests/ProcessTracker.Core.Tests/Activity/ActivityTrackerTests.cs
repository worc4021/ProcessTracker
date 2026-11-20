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
    public void MatlabJvmNoise_NotActive()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.03f, ctx: 1500), "MATLAB.exe");
        Assert.Null(tracker.Get(k).LastActiveAt);
    }

    [Fact]
    public void ImageLookupIsCaseInsensitive()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var k = Key(1);
        tracker.Update(Sample(k, T, cpu: 0.03f, ctx: 1500), "matlab.EXE");
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
        var dymola = Key(1); var child = Key(2); var dymosim = Key(3);
        var table = new ProcessTable();
        table.SetLive([(dymola, null), (child, dymola), (dymosim, child)]);

        tracker.Update(Sample(dymola, T), "Dymola.exe");
        tracker.Update(Sample(dymosim, T.AddSeconds(5), cpu: 1f), "dymosim.exe");

        Assert.Equal(T.AddSeconds(5), tracker.EffectiveLastActive(dymola, table));
    }

    [Fact]
    public void EffectiveLastActive_IgnoresDeadDescendants()
    {
        var tracker = new ActivityTracker(new ActivityOptions());
        var parent = Key(1); var child = Key(2);
        var table = new ProcessTable();
        table.SetLive([(parent, null), (child, parent)]);
        tracker.Update(Sample(parent, T, cpu: 1f), "Dymola.exe");
        tracker.Update(Sample(child, T.AddSeconds(5), cpu: 1f), "dymosim.exe");

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
