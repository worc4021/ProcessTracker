namespace ProcessTracker.Core.Activity;

/// <summary>Per-sample thresholds above which a signal counts as activity.</summary>
public sealed record ActivityThresholds(
    float CpuCores = 0.01f, long IoOps = 1, long WriteOps = 1, long ContextSwitches = 100);

/// <summary>Default and per-image activity thresholds. Image lookup is case-insensitive.</summary>
public sealed class ActivityOptions
{
    public ActivityThresholds Default { get; set; } = new();

    /// <summary>Thresholds for images that differ from <see cref="Default"/>; empty by default.</summary>
    public Dictionary<string, ActivityThresholds> PerImage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
