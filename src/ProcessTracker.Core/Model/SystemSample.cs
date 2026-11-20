namespace ProcessTracker.Core.Model;

public readonly record struct SystemSample(
    DateTime TimestampUtc, float CpuBusyPercent, float BusyCores, int LogicalCpuCount,
    long CommitChargeBytes, long CommitLimitBytes,
    long AvailablePhysicalBytes, long TotalPhysicalBytes);
