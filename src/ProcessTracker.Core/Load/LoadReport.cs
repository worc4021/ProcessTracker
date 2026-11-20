namespace ProcessTracker.Core.Load;

public enum Pressure { Low, Moderate, High, Critical }

/// <summary>One session's resource use, as shown in a load report.</summary>
public sealed record SessionUsage(string UnitId, string? Title, string? Cwd, float CpuCores, double PrivateGb, int ProcessCount);

/// <summary>Machine-pressure report that agent sessions use to size their own parallelism.</summary>
public sealed record LoadReport(
    DateTime TimestampUtc,
    Pressure Pressure,
    float CpuBusyPercent,
    float FreeCores,
    double AvailableGb,
    double CommitHeadroomGb,
    int SuggestedParallelism,
    SessionUsage? Caller,
    IReadOnlyList<SessionUsage> TopSessions);
