using System.Text.RegularExpressions;
using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Hooks;

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

    /// <summary>The run mode, from the command line only (a Mode in a settings file is ignored).</summary>
    public RunMode Mode { get; private set; }

    /// <summary>
    /// Loopback port; 0 means the mode default (47811, or 47812 with --dev). Only the command line can set it (for
    /// development and tests): the installer, upgrade task and CLI all assume 47811, so a Port in an appsettings.json
    /// is ignored.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Data folder; empty means %ProgramData%\ProcessTracker, or %LOCALAPPDATA%\ProcessTracker with --user, or
    /// %LOCALAPPDATA%\ProcessTracker-dev with --dev.
    /// </summary>
    public string DataDir { get; set; } = "";

    /// <summary>Profile whose .claude\projects transcripts are read; empty means each session's profile is that of its anchor's owner.</summary>
    public string UserProfilePath { get; set; } = "";

    /// <summary>History retention in hours; values below 2 are raised to 2.</summary>
    public double RetentionHours { get; set; } = 24;

    /// <summary>Idle minutes after which an orphan is alertable.</summary>
    public double OrphanIdleAlertMinutes { get; set; } = 30;

    /// <summary>
    /// Image-name patterns (<c>*</c> and <c>?</c> wildcards, case-insensitive) that limit orphan alerts. Empty (the
    /// default) means every orphan idle for <see cref="OrphanIdleAlertMinutes"/> is alertable.
    /// </summary>
    public string[] OrphanAlertImages { get; set; } = [];

    /// <summary>
    /// Per-image activity thresholds, keyed by image name (case-insensitive). Fields left out take the defaults of
    /// <see cref="Core.Activity.ActivityThresholds"/>. Empty by default: every image uses the default thresholds.
    /// </summary>
    public Dictionary<string, ActivityThresholdsSetting> ActivityThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="ActivityThresholds"/> as tracker options (call only after <see cref="Validate"/> passed).</summary>
    public ActivityOptions ToActivityOptions()
    {
        var defaults = new Core.Activity.ActivityThresholds();
        var options = new ActivityOptions();
        foreach (var (image, s) in ActivityThresholds)
            options.PerImage[image] = new Core.Activity.ActivityThresholds(
                s.CpuCores ?? defaults.CpuCores, s.IoOps ?? defaults.IoOps,
                s.WriteOps ?? defaults.WriteOps, s.ContextSwitches ?? defaults.ContextSwitches);
        return options;
    }

    /// <summary>
    /// Extra regular expressions (case-insensitive) that mark a Bash or PowerShell command as heavy work for the
    /// PreToolUse hook, on top of the built-in list in <see cref="HeavyCommandClassifier"/>.
    /// </summary>
    public string[] HeavyCommandPatterns { get; set; } = [];

    private Regex[]? _heavyCommandRegexes;

    /// <summary><see cref="HeavyCommandPatterns"/> compiled once (call only after <see cref="Validate"/> passed).</summary>
    public IReadOnlyList<Regex> HeavyCommandRegexes() =>
        _heavyCommandRegexes ??= HeavyCommandPatterns.Select(HeavyCommandClassifier.CreatePattern).ToArray();

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
        for (var i = 0; i < OrphanAlertImages.Length; i++)
            if (string.IsNullOrWhiteSpace(OrphanAlertImages[i]))
                errors.Add($"OrphanAlertImages[{i}] must not be empty");
        foreach (var (image, s) in ActivityThresholds)
        {
            if (string.IsNullOrWhiteSpace(image)) errors.Add("ActivityThresholds must not have an empty image name");
            if (s is null) { errors.Add($"ActivityThresholds[{image}] must be an object"); continue; }
            if (s.CpuCores is { } c && !(float.IsFinite(c) && c >= 0)) errors.Add($"ActivityThresholds[{image}] CpuCores must be >= 0 (was {c})");
            if (s.IoOps is < 0) errors.Add($"ActivityThresholds[{image}] IoOps must be >= 0 (was {s.IoOps})");
            if (s.WriteOps is < 0) errors.Add($"ActivityThresholds[{image}] WriteOps must be >= 0 (was {s.WriteOps})");
            if (s.ContextSwitches is < 0) errors.Add($"ActivityThresholds[{image}] ContextSwitches must be >= 0 (was {s.ContextSwitches})");
        }
        for (var i = 0; i < HeavyCommandPatterns.Length; i++)
        {
            // An empty pattern would match every command.
            if (string.IsNullOrWhiteSpace(HeavyCommandPatterns[i]))
            {
                errors.Add($"HeavyCommandPatterns[{i}] must not be empty");
                continue;
            }
            try { HeavyCommandClassifier.CreatePattern(HeavyCommandPatterns[i]); }
            catch (ArgumentException e) { errors.Add($"HeavyCommandPatterns[{i}] is not a valid regular expression: {e.Message}"); }
        }
        return errors;
    }

    /// <summary>True when a settings file sets <see cref="Port"/> that the command line does not (it is ignored).</summary>
    public static bool HasIgnoredPort(IConfiguration config, IConfiguration commandLine) =>
        config[nameof(Port)] is not null && commandLine[nameof(Port)] is null;

    /// <param name="config">All configuration (settings files and command line).</param>
    /// <param name="commandLine">The command line alone: the only source of <see cref="Port"/>.</param>
    /// <param name="mode">The run mode (see <see cref="RunModes.ParseMode"/>).</param>
    public static ServiceOptions Resolve(IConfiguration config, IConfiguration commandLine, RunMode mode)
    {
        var o = new ServiceOptions();
        config.Bind(o);
        o.Mode = mode;
        o.Port = commandLine.GetValue<int>(nameof(Port));
        if (o.Port == 0) o.Port = mode == RunMode.Dev ? DevPort : ProductionPort;
        if (string.IsNullOrWhiteSpace(o.DataDir)) o.DataDir = DefaultDataDir(mode);
        return o;
    }

    /// <summary>The data folder of a mode when DataDir is not set; its appsettings.json is the second settings file.</summary>
    public static string DefaultDataDir(RunMode mode) => mode switch
    {
        RunMode.Service => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker"),
        RunMode.User => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker"),
        RunMode.Dev => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker-dev"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

/// <summary>One image's activity thresholds in settings; a field left out takes the default.</summary>
public sealed class ActivityThresholdsSetting
{
    /// <summary>Cores of CPU per sample above which the image counts as active.</summary>
    public float? CpuCores { get; set; }

    /// <summary>I/O operations per sample above which the image counts as active.</summary>
    public long? IoOps { get; set; }

    /// <summary>Write operations per sample above which the image counts as active.</summary>
    public long? WriteOps { get; set; }

    /// <summary>Context switches per sample above which the image counts as active.</summary>
    public long? ContextSwitches { get; set; }
}
