using System.Diagnostics;

namespace ProcessTracker.Updater;

/// <summary>The result of an upgrade; the values are the updater's exit codes.</summary>
public enum UpgradeOutcome { Upgraded = 0, RolledBack = 1, RollbackFailed = 2 }

/// <summary>
/// Moves the service to a new version folder: stop, repoint <c>current</c>, start, and wait for a healthy report of the
/// new version; on any failure, repoint to the previous version and wait for that one instead. After a successful
/// upgrade, old version folders are pruned; a pruning failure is logged and never turns the upgrade into a rollback.
/// </summary>
public sealed class UpgradeOrchestrator(
    IServiceControl service, IJunction junction, IHealthProbe health, Action<string>? log = null, TimeSpan? pollInterval = null)
{
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    public const double MaxTickAgeSeconds = 5;
    public const int VersionsToKeep = 3;

    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>
    /// Upgrades the service under <paramref name="installRoot"/> to <c>versions\<paramref name="newVersion"/></c>.
    /// Throws, before touching anything, <see cref="DirectoryNotFoundException"/> if that folder does not exist and
    /// <see cref="InvalidOperationException"/> if <c>current</c> points nowhere (there would be nothing to roll back to).
    /// </summary>
    public async Task<UpgradeOutcome> RunAsync(string installRoot, string newVersion, TimeSpan healthTimeout)
    {
        var link = Path.Combine(installRoot, "current");
        var versionsDir = Path.Combine(installRoot, "versions");
        var newTarget = Path.Combine(versionsDir, newVersion);
        if (!Directory.Exists(newTarget)) throw new DirectoryNotFoundException($"Version folder {newTarget} does not exist.");

        var previous = junction.Target(link)
                       ?? throw new InvalidOperationException($"{link} does not point at a version folder; nothing to roll back to.");
        _log($"Upgrading {link} from {previous} to {newTarget}");

        var healthy = false;
        try
        {
            _log("Stopping the service");
            service.Stop(StopTimeout);
            _log($"Pointing {link} at {newTarget}");
            junction.Point(link, newTarget);
            _log("Starting the service");
            service.Start();
            healthy = await WaitHealthyAsync(newVersion, healthTimeout);
            if (!healthy) _log($"Version {newVersion} did not report healthy within {healthTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception e)
        {
            _log($"Upgrade failed: {e.Message}");
        }

        if (!healthy) return await RollBackAsync(link, previous, healthTimeout);

        _log($"Version {newVersion} is healthy");
        Prune(versionsDir, newTarget, previous);
        return UpgradeOutcome.Upgraded;
    }

    private async Task<UpgradeOutcome> RollBackAsync(string link, string previous, TimeSpan healthTimeout)
    {
        var oldVersion = Path.GetFileName(Path.TrimEndingDirectorySeparator(previous));
        _log($"Rolling back to {previous}");
        try
        {
            try { service.Stop(StopTimeout); }
            catch (Exception e) { _log($"Stopping the service during rollback failed: {e.Message}; continuing"); }
            junction.Point(link, previous);
            service.Start();
        }
        catch (Exception e)
        {
            _log($"Rollback failed: {e.Message}");
            return UpgradeOutcome.RollbackFailed;
        }

        if (await WaitHealthyAsync(oldVersion, healthTimeout))
        {
            _log($"Rolled back; version {oldVersion} is healthy");
            return UpgradeOutcome.RolledBack;
        }
        _log($"Rollback failed: version {oldVersion} did not report healthy within {healthTimeout.TotalSeconds:0.#} s");
        return UpgradeOutcome.RollbackFailed;
    }

    /// <summary>Polls health until it reports <paramref name="version"/> with a fresh tick, or the timeout passes.</summary>
    private async Task<bool> WaitHealthyAsync(string version, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        HealthResult? last = null;
        while (true)
        {
            try { last = await health.GetAsync(); }
            catch (Exception e) { _log($"Health probe failed: {e.Message}"); last = null; }
            if (last is not null && IsHealthy(last, version)) return true;
            if (clock.Elapsed >= timeout) break;
            await Task.Delay(_pollInterval);
        }
        _log(last is null ? "Health: no answer" : $"Health: version {last.Version}, tick age {last.TickAgeSeconds:0.#} s");
        return false;
    }

    /// <summary>
    /// The reported version matches when the part before any '+' (build metadata such as a source revision) equals
    /// <paramref name="version"/>, and the latest collector tick is fresh.
    /// </summary>
    public static bool IsHealthy(HealthResult health, string version)
    {
        var reported = health.Version.Split('+', 2)[0];
        return string.Equals(reported, version, StringComparison.OrdinalIgnoreCase)
               && health.TickAgeSeconds < MaxTickAgeSeconds;
    }

    /// <summary>
    /// Deletes version folders other than the <see cref="VersionsToKeep"/> newest by creation time, never the current
    /// or previous target. A folder that cannot be deleted (e.g. a locked file) is logged and skipped; any other
    /// failure is logged and ends pruning. Never throws: the upgrade has already succeeded.
    /// </summary>
    private void Prune(string versionsDir, string current, string previous)
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(current), Normalize(previous) };
            var doomed = new DirectoryInfo(versionsDir).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc)
                .Skip(VersionsToKeep)
                .Where(d => !keep.Contains(Normalize(d.FullName)));
            foreach (var dir in doomed)
            {
                try
                {
                    _log($"Pruning {dir.FullName}");
                    dir.Delete(recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log($"Could not prune {dir.FullName}: {e.Message}; leaving it");
                }
            }
        }
        catch (Exception e)
        {
            try { _log($"Could not prune old versions: {e.Message}"); }
            catch { /* logging must not undo a healthy upgrade either */ }
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
