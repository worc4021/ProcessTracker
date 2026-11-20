using ProcessTracker.Core.Load;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Tests.Load;

public sealed class LoadAssessorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SystemSample Sys(float cpu, long limitGb, long headroomGb, int cores = 16) =>
        new(T0, cpu, 0, cores, (limitGb - headroomGb) * Gb, limitGb * Gb, 8 * Gb, 32 * Gb);

    private static TickFrame Frame(float cpu) => new(T0, Sys(cpu, 20, 9), [], []);

    private static TrackerView View(SystemSample sys, params SessionView[] sessions) =>
        new(T0, sys, new Dictionary<ProcessKey, ProcessView>(), sessions);

    private static SessionView Session(string id, long privateBytes, bool live = true) =>
        new(new SessionInfo(id, null, [], null, T0, live ? null : T0, @"C:\work\" + id, "title " + id, null),
            live, 1.5f, privateBytes, 3);

    [Fact]
    public void LowLoad_ParallelismLimitedByCoresAndMemory()
    {
        var sys = Sys(10, 20, 9);
        var r = LoadAssessor.Assess(View(sys), [Frame(10)], null, new LoadOptions());
        Assert.Equal(Pressure.Low, r.Pressure);
        Assert.Equal(14.4f, r.FreeCores, 0.01f);
        Assert.Equal(9.0, r.CommitHeadroomGb, 6);
        Assert.Equal(4, r.SuggestedParallelism);
    }

    [Fact]
    public void CommitBelow5Percent_IsCritical_Parallelism1()
    {
        var sys = Sys(10, 100, 4); // 4% free
        var r = LoadAssessor.Assess(View(sys), [Frame(10)], null, new LoadOptions());
        Assert.Equal(Pressure.Critical, r.Pressure);
        Assert.Equal(1, r.SuggestedParallelism);
    }

    [Fact]
    public void CpuSpikeInOneFrame_AveragedOut_NotHigh()
    {
        var recent = new[] { Frame(100), Frame(20), Frame(20), Frame(20), Frame(20) };
        var r = LoadAssessor.Assess(View(Sys(100, 20, 9)), recent, null, new LoadOptions());
        Assert.Equal(36f, r.CpuBusyPercent, 0.01f);
        Assert.Equal(Pressure.Low, r.Pressure);
    }

    [Fact]
    public void Caller_IsFilledFromUnit()
    {
        var view = View(Sys(10, 20, 9), Session("a", 2 * Gb), Session("b", 1 * Gb, live: false));
        var r = LoadAssessor.Assess(view, [], "b", new LoadOptions());
        Assert.NotNull(r.Caller);
        Assert.Equal("b", r.Caller!.UnitId);
        Assert.Equal("title b", r.Caller.Title);
        Assert.Equal(1.0, r.Caller.PrivateGb, 6);
        Assert.Null(LoadAssessor.Assess(view, [], "zzz", new LoadOptions()).Caller);
        Assert.Null(LoadAssessor.Assess(view, [], null, new LoadOptions()).Caller);
    }

    [Fact]
    public void TopSessions_OrderedByMemory_ExcludesEnded()
    {
        var view = View(Sys(10, 20, 9),
            Session("small", 1 * Gb), Session("ended", 9 * Gb, live: false), Session("big", 3 * Gb));
        var r = LoadAssessor.Assess(view, [], null, new LoadOptions());
        Assert.Equal(["big", "small"], r.TopSessions.Select(s => s.UnitId));
    }

    [Fact]
    public void EmptyView_DoesNotDivideByZero_Parallelism1()
    {
        var r = LoadAssessor.Assess(TrackerView.Empty, [], null, new LoadOptions());
        Assert.Equal(Pressure.Low, r.Pressure);
        Assert.Equal(1, r.SuggestedParallelism);
        Assert.Equal(0, r.CommitHeadroomGb);
    }
}
