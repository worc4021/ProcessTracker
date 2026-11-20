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
    private const double MaxRetentionHours = 24 * 30;
    private const double MaxOrphanIdleAlertMinutes = 7 * 24 * 60;

    /// <summary>
    /// Loopback port; 0 means the mode default (47811, or 47812 with --dev). Only the command line can set it (for
    /// development and tests): the updater, install script and CLI all assume 47811, so a Port in an appsettings.json
    /// is ignored.
    /// </summary>
    public int Port { get; set; }

    /// <summary>Data folder; empty means %ProgramData%\ProcessTracker (or %LOCALAPPDATA%\ProcessTracker-dev with --dev).</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Profile whose .claude\projects transcripts are read; empty means each session's profile is that of its anchor's owner.</summary>
    public string UserProfilePath { get; set; } = "";

    /// <summary>History retention in hours; values below 2 are raised to 2.</summary>
    public double RetentionHours { get; set; } = 24;

    public double OrphanIdleAlertMinutes { get; set; } = 30;

    public TimeSpan Retention => TimeSpan.FromHours(Math.Max(MinRetentionHours, RetentionHours));

    public TimeSpan OrphanIdleAlert => TimeSpan.FromMinutes(OrphanIdleAlertMinutes);

    /// <summary>Problems with the resolved values; empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Port is < 1 or > 65535) errors.Add($"Port must be 1-65535 (was {Port})");
        if (!double.IsFinite(RetentionHours) || RetentionHours <= 0 || RetentionHours > MaxRetentionHours)
            errors.Add($"RetentionHours must be > 0 and <= {MaxRetentionHours} (was {RetentionHours}); values below {MinRetentionHours} are raised to {MinRetentionHours}");
        if (!double.IsFinite(OrphanIdleAlertMinutes) || OrphanIdleAlertMinutes <= 0 || OrphanIdleAlertMinutes > MaxOrphanIdleAlertMinutes)
            errors.Add($"OrphanIdleAlertMinutes must be > 0 and <= {MaxOrphanIdleAlertMinutes} (was {OrphanIdleAlertMinutes})");
        return errors;
    }

    /// <summary>True when a settings file sets <see cref="Port"/> that the command line does not (it is ignored).</summary>
    public static bool HasIgnoredPort(IConfiguration config, IConfiguration commandLine) =>
        config[nameof(Port)] is not null && commandLine[nameof(Port)] is null;

    /// <param name="config">All configuration (settings files and command line).</param>
    /// <param name="commandLine">The command line alone: the only source of <see cref="Port"/>.</param>
    /// <param name="dev">Development mode (--dev).</param>
    public static ServiceOptions Resolve(IConfiguration config, IConfiguration commandLine, bool dev)
    {
        var o = new ServiceOptions();
        config.Bind(o);
        o.Port = commandLine.GetValue<int>(nameof(Port));
        if (o.Port == 0) o.Port = dev ? DevPort : ProductionPort;
        if (string.IsNullOrWhiteSpace(o.DataDir))
        {
            o.DataDir = dev
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker-dev")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker");
        }
        return o;
    }
}
