using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Load;

public static class LoadAssessor
{
    // Thresholds (spec section 16); tune here.
    private const double CriticalHeadroomFraction = 0.05;
    private const double CriticalHeadroomGb = 2;
    private const double HighHeadroomFraction = 0.15;
    private const double HighCpuPercent = 90;
    private const double ModerateHeadroomFraction = 0.30;
    private const double ModerateCpuPercent = 70;

    private const double BytesPerGb = 1024.0 * 1024 * 1024;

    public static LoadReport Assess(
        TrackerView view, IReadOnlyList<TickFrame> recent, string? callerUnitId, LoadOptions o)
    {
        var sys = view.System;

        float cpu = recent.Count > 0 ? (float)recent.Average(f => f.System.CpuBusyPercent) : sys.CpuBusyPercent;

        double headroomBytes = sys.CommitLimitBytes > 0 ? sys.CommitLimitBytes - sys.CommitChargeBytes : 0;
        double headroomGb = headroomBytes / BytesPerGb;
        // No commit data (limit 0): treat the fraction as fully free so only the absolute rule can apply.
        double h = sys.CommitLimitBytes > 0 ? headroomBytes / sys.CommitLimitBytes : 1;

        var pressure =
            sys.CommitLimitBytes > 0 && (h < CriticalHeadroomFraction || headroomGb < CriticalHeadroomGb) ? Pressure.Critical
            : h < HighHeadroomFraction || cpu > HighCpuPercent ? Pressure.High
            : h < ModerateHeadroomFraction || cpu > ModerateCpuPercent ? Pressure.Moderate
            : Pressure.Low;

        float freeCores = sys.LogicalCpuCount * (1 - cpu / 100f);

        int parallelism = pressure >= Pressure.High
            ? 1
            : Math.Max(1, (int)Math.Min(Math.Floor(freeCores), Math.Floor(headroomGb / o.PerTaskGb)));

        var live = view.Sessions.Where(s => s.IsLive);
        var top = live.OrderByDescending(s => s.PrivateBytes).Take(o.TopSessions).Select(ToUsage).ToList();
        var callerView = callerUnitId is null ? null : view.Sessions.FirstOrDefault(s => s.Info.UnitId == callerUnitId);

        return new LoadReport(
            view.TimestampUtc, pressure, cpu, freeCores,
            sys.AvailablePhysicalBytes / BytesPerGb, headroomGb, parallelism,
            callerView is null ? null : ToUsage(callerView), top);
    }

    private static SessionUsage ToUsage(SessionView s) =>
        new(s.Info.UnitId, s.Info.Title, s.Info.Cwd, s.CpuCores, s.PrivateBytes / BytesPerGb, s.ProcessCount);
}
