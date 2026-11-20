namespace ProcessTracker.Core.Model;

/// <summary>Cumulative per-process counters at one instant. Times are in 100 ns units.</summary>
public sealed record RawProcessCounters(
    ProcessKey Key, int ParentPid, string ImageName,
    long KernelTime, long UserTime, long PrivateBytes, long WorkingSet,
    long ReadOps, long WriteOps, long OtherOps,
    long ReadBytes, long WriteBytes, long OtherBytes,
    long ContextSwitches, int HandleCount, int ThreadCount);
