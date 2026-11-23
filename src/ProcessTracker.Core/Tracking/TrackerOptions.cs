using ProcessTracker.Core.Activity;

namespace ProcessTracker.Core.Tracking;

/// <summary>Tracker configuration. History lives in <c>DataDir\history</c>, state in <c>DataDir\state.db</c>.</summary>
public sealed class TrackerOptions
{
    public string DataDir { get; set; } = "";

    /// <summary>History retention; values below 2 h are raised to 2 h.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Effective idle time after which an orphaned process is alertable.</summary>
    public TimeSpan OrphanIdleAlert { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Image-name patterns (<c>*</c> and <c>?</c> wildcards, case-insensitive) that limit orphan alerts. Empty means
    /// every orphan idle for <see cref="OrphanIdleAlert"/> is alertable.
    /// </summary>
    public string[] OrphanAlertImages { get; set; } = [];

    /// <summary>
    /// Profile whose transcripts (under <c>.claude\projects</c>) are read for every session. Empty means each session's
    /// profile comes from the owner of its anchor via the tracker's profile resolver (none: transcript reads are off).
    /// </summary>
    public string UserProfilePath { get; set; } = "";

    public ActivityOptions Activity { get; set; } = new();
}
