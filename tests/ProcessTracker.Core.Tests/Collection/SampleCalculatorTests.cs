using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests.Collection;

public class SampleCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static RawProcessCounters Raw(long cpu100ns, long readOps, long ctx) => new(
        new ProcessKey(1, 1), ParentPid: 0, ImageName: "test.exe",
        KernelTime: 0, UserTime: cpu100ns, PrivateBytes: 0, WorkingSet: 0,
        ReadOps: readOps, WriteOps: 0, OtherOps: 0, ReadBytes: 0, WriteBytes: 0, OtherBytes: 0,
        ContextSwitches: ctx, HandleCount: 0, ThreadCount: 0);

    private static SystemCounters Sys(long idle, long kernel, long user) => new(
        LogicalCpuCount: 4, IdleTime: idle, KernelTime: kernel, UserTime: user,
        CommitChargeBytes: 0, CommitLimitBytes: 0, AvailablePhysicalBytes: 0, TotalPhysicalBytes: 0);

    [Fact] public void CpuCores_IsCpuTimeOverElapsed()
    {   // 2 s elapsed, 3 s of CPU time (kernel+user) -> 1.5 cores
        var s = SampleCalculator.Compute(Raw(0,0,0), T0, Raw(30_000_000,0,0), T0.AddSeconds(2));
        Assert.Equal(1.5f, s.CpuCores, 3);
    }

    [Fact] public void LongGapAfterResume_UsesRealElapsed()
    {   // 1 hour gap, 36 s CPU -> 0.01 cores, not 18 cores
        var s = SampleCalculator.Compute(Raw(0,0,0), T0, Raw(360_000_000,0,0), T0.AddHours(1));
        Assert.Equal(0.01f, s.CpuCores, 4);
    }

    [Fact] public void CounterRegression_ClampsToZero()
    {
        var s = SampleCalculator.Compute(Raw(50,10,10), T0, Raw(40,5,5), T0.AddSeconds(2));
        Assert.Equal(0f, s.CpuCores); Assert.Equal(0, s.ReadOpsDelta); Assert.Equal(0, s.ContextSwitchesDelta);
    }

    [Fact] public void ZeroElapsed_GivesZeroCpu()
        => Assert.Equal(0f, SampleCalculator.Compute(Raw(0,0,0), T0, Raw(100,0,0), T0).CpuCores);

    [Fact] public void First_HasNoDeltas()
    {
        var s = SampleCalculator.First(Raw(999,999,999), T0);
        Assert.Equal(0f, s.CpuCores); Assert.Equal(0, s.ReadOpsDelta);
    }

    [Fact] public void System_BusyPercent_ExcludesIdle()
    {   // 4 CPUs, 2 s: total = kernel+user deltas = 80M (kernel includes idle 60M) -> busy 25%, 1 busy core
        var a = Sys(idle:0, kernel:0, user:0);
        var b = Sys(idle:60_000_000, kernel:70_000_000, user:10_000_000);
        var s = SampleCalculator.ComputeSystem(a, b, T0);
        Assert.Equal(25f, s.CpuBusyPercent, 2); Assert.Equal(1f, s.BusyCores, 2);
    }
}
