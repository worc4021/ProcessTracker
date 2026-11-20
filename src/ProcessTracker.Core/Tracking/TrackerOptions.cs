using ProcessTracker.Core.Activity;

namespace ProcessTracker.Core.Tracking;

/// <summary>Tracker configuration. History lives in <c>DataDir\history</c>, state in <c>DataDir\state.db</c>.</summary>
public sealed class TrackerOptions
{
    public string DataDir { get; set; } = "";

    /// <summary>History retention; values below 2 h are raised to 2 h.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Effective idle time after which an orphaned watched process is alertable.</summary>
    public TimeSpan OrphanIdleAlert { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Profile of the interactive user (transcripts under <c>.claude\projects</c>); empty disables transcript reads.</summary>
    public string UserProfilePath { get; set; } = "";

    public ActivityOptions Activity { get; set; } = new();
}
