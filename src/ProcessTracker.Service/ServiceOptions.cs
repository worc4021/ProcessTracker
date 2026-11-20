namespace ProcessTracker.Service;

/// <summary>
/// Service settings, bound from configuration (keys are the property names). Unset values get mode-dependent
/// defaults in <see cref="Resolve"/>.
/// </summary>
public sealed class ServiceOptions
{
    public const int ProductionPort = 47811;
    public const int DevPort = 47812;
    private const double MinRetentionHours = 2;

    /// <summary>Loopback port; 0 means the mode default (47811, or 47812 with --dev).</summary>
    public int Port { get; set; }

    /// <summary>Data folder; empty means %ProgramData%\ProcessTracker (or %LOCALAPPDATA%\ProcessTracker-dev with --dev).</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Profile whose .claude\projects transcripts are read; empty means the current user's profile.</summary>
    public string UserProfilePath { get; set; } = "";

    /// <summary>History retention in hours; values below 2 are raised to 2.</summary>
    public double RetentionHours { get; set; } = 24;

    public double OrphanIdleAlertMinutes { get; set; } = 30;

    public TimeSpan Retention => TimeSpan.FromHours(Math.Max(MinRetentionHours, RetentionHours));

    public TimeSpan OrphanIdleAlert => TimeSpan.FromMinutes(OrphanIdleAlertMinutes);

    public static ServiceOptions Resolve(IConfiguration config, bool dev)
    {
        var o = new ServiceOptions();
        config.Bind(o);
        if (o.Port == 0) o.Port = dev ? DevPort : ProductionPort;
        if (string.IsNullOrWhiteSpace(o.DataDir))
        {
            o.DataDir = dev
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker-dev")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker");
        }
        if (string.IsNullOrWhiteSpace(o.UserProfilePath))
            o.UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return o;
    }
}
