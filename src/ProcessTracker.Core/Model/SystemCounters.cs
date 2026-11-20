namespace ProcessTracker.Core.Model;

/// <summary>Cumulative system-wide counters. KernelTime includes IdleTime, as GetSystemTimes reports it.</summary>
public sealed record SystemCounters(
    int LogicalCpuCount, long IdleTime, long KernelTime, long UserTime,
    long CommitChargeBytes, long CommitLimitBytes,
    long AvailablePhysicalBytes, long TotalPhysicalBytes);
