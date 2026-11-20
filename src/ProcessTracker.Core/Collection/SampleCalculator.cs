using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Collection;

/// <summary>Turns cumulative counters into per-interval samples. Uses the real elapsed time between snapshots.</summary>
public static class SampleCalculator
{
    public static ProcessSample Compute(RawProcessCounters prev, DateTime prevTs, RawProcessCounters cur, DateTime curTs)
    {
        var elapsedTicks = (curTs - prevTs).Ticks;
        var cpuDelta = Delta(prev.KernelTime + prev.UserTime, cur.KernelTime + cur.UserTime);
        var cores = elapsedTicks <= 0 ? 0f : (float)((double)cpuDelta / elapsedTicks);

        return new ProcessSample(
            cur.Key, curTs, cores, cur.PrivateBytes, cur.WorkingSet,
            Delta(prev.ReadOps, cur.ReadOps), Delta(prev.WriteOps, cur.WriteOps), Delta(prev.OtherOps, cur.OtherOps),
            Delta(prev.ReadBytes, cur.ReadBytes), Delta(prev.WriteBytes, cur.WriteBytes), Delta(prev.OtherBytes, cur.OtherBytes),
            Delta(prev.ContextSwitches, cur.ContextSwitches), cur.HandleCount, cur.ThreadCount);
    }

    public static ProcessSample First(RawProcessCounters cur, DateTime ts) => new(
        cur.Key, ts, 0f, cur.PrivateBytes, cur.WorkingSet,
        0, 0, 0, 0, 0, 0, 0, cur.HandleCount, cur.ThreadCount);

    public static SystemSample ComputeSystem(SystemCounters prev, SystemCounters cur, DateTime ts)
    {
        // Kernel time already includes idle time, so kernel + user is the total.
        var total = Delta(prev.KernelTime + prev.UserTime, cur.KernelTime + cur.UserTime);
        var idle = Math.Min(Delta(prev.IdleTime, cur.IdleTime), total);
        var busyPercent = total == 0 ? 0f : (float)((double)(total - idle) / total * 100.0);
        var busyCores = busyPercent / 100f * cur.LogicalCpuCount;

        return new SystemSample(
            ts, busyPercent, busyCores, cur.LogicalCpuCount,
            cur.CommitChargeBytes, cur.CommitLimitBytes, cur.AvailablePhysicalBytes, cur.TotalPhysicalBytes);
    }

    private static long Delta(long prev, long cur) => cur >= prev ? cur - prev : 0;
}
