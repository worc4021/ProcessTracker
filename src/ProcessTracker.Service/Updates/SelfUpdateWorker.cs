using System.ComponentModel;
using System.IO.Compression;
using ProcessTracker.Core.Updates;
using ProcessTracker.Native;

namespace ProcessTracker.Service.Updates;

/// <summary>What one update check did.</summary>
public enum UpdateCheck { MachineService, NoRelease, UpToDate, PreviouslyFailed, DownloadFailed, ExtractFailed, StartFailed, Started }

/// <summary>Everything <see cref="SelfUpdateWorker"/> touches outside its own logic; tests replace all of it.</summary>
public sealed class SelfUpdateEnvironment
{
    /// <summary>The running version's <c>app-&lt;X.Y.Z&gt;</c> folder.</summary>
    public required string CurrentAppDir { get; init; }
    public required Version CurrentVersion { get; init; }
    /// <summary><c>%LOCALAPPDATA%\Programs\ProcessTracker</c>, where new versions are extracted.</summary>
    public required string InstallRoot { get; init; }
    /// <summary>Where downloads go (<c>%TEMP%</c>); each version gets its own subfolder, deleted afterwards.</summary>
    public required string TempDir { get; init; }
    public required Func<bool> MachineServiceInstalled { get; init; }
    public required IProcessStarter Starter { get; init; }
    public TimeSpan FirstCheck { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Bounds one check's release lookup and download, so a stalled transfer cannot hold updates back.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// User mode only: 10 minutes after start, then every 24 hours, looks for a newer GitHub release. A newer one is
/// downloaded (hash-verified by <see cref="ReleaseClient"/>), extracted next to this version as <c>app-&lt;X.Y.Z&gt;</c>
/// and started with <c>--user --takeover &lt;this folder&gt;</c>. This worker never stops the host itself: the new
/// version sets the exit event, which does. Nothing happens while the machine-wide service is installed.
/// </summary>
public sealed class SelfUpdateWorker(
    ReleaseClient releases, SelfUpdateEnvironment env, TimeProvider time, ILogger<SelfUpdateWorker> log) : BackgroundService
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\Programs\ProcessTracker</c> when <paramref name="appDir"/> is an <c>app-*</c> folder directly
    /// in it; otherwise null (a build output or the MSI's folder), and the program does not self-update.
    /// </summary>
    public static string? InstallRootFor(string appDir, string localAppData)
    {
        if (!Path.IsPathFullyQualified(appDir) || !Path.IsPathFullyQualified(localAppData)) return null;
        var dir = AppFolders.Normalize(appDir);
        if (!AppFolders.IsAppFolderName(Path.GetFileName(dir))) return null;
        var root = Path.GetDirectoryName(dir);
        var expected = Path.Combine(AppFolders.Normalize(localAppData), "Programs", AppFolders.RootName);
        return root is not null && string.Equals(root, expected, StringComparison.OrdinalIgnoreCase) ? root : null;
    }

    /// <summary>The X.Y.Z of an informational version such as <c>0.3.0</c> or <c>0.3.0-dev.202611231200</c>; null otherwise.</summary>
    public static Version? NumericVersion(string informational)
    {
        var core = informational.Split('+', '-')[0];
        return Version.TryParse(core, out var v) && v.Build >= 0 && v.Revision < 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(env.FirstCheck, time, stoppingToken);
            while (true)
            {
                try { await CheckOnceAsync(stoppingToken); }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested && e is not OutOfMemoryException)
                {
                    log.LogError(e, "The update check failed; trying again in {Hours} h", env.Interval.TotalHours);
                }
                await Task.Delay(env.Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<UpdateCheck> CheckOnceAsync(CancellationToken ct)
    {
        if (env.MachineServiceInstalled())
        {
            log.LogInformation("The machine-wide ProcessTracker service is installed; not updating user mode");
            return UpdateCheck.MachineService;
        }

        // One stalled transfer must not hold updates back until the next logon: the lookup and the download share a deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(env.DownloadTimeout);
        try
        {
            return await CheckBoundedAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("The update check did not finish within {Minutes} min; nothing changed, trying again later",
                env.DownloadTimeout.TotalMinutes);
            return UpdateCheck.DownloadFailed;
        }
    }

    private async Task<UpdateCheck> CheckBoundedAsync(CancellationToken ct)
    {
        var latest = await releases.GetLatestAsync(ct);
        if (latest is null)
        {
            log.LogInformation("No usable release information from GitHub; checking again later");
            return UpdateCheck.NoRelease;
        }
        if (!ReleaseClient.IsNewer(latest.Version, env.CurrentVersion))
        {
            log.LogInformation("Up to date: running {Current}, latest release {Latest}", env.CurrentVersion, latest.Version);
            return UpdateCheck.UpToDate;
        }

        var version = $"{latest.Version.Major}.{latest.Version.Minor}.{latest.Version.Build}";
        var target = Path.Combine(env.InstallRoot, AppFolders.Prefix + version);
        if (AppFolders.SamePath(target, env.CurrentAppDir)) return UpdateCheck.UpToDate; // never replace our own folder
        if (File.Exists(Path.Combine(target, Takeover.FailedMarkerName)))
        {
            log.LogWarning("ProcessTracker {Version} failed to take over before ({Marker} in {Dir}); not trying it again",
                version, Takeover.FailedMarkerName, target);
            return UpdateCheck.PreviouslyFailed;
        }

        log.LogInformation("ProcessTracker {Version} is available (running {Current}); downloading", version, env.CurrentVersion);
        var downloadDir = Path.Combine(env.TempDir, "ProcessTracker-update-" + version);
        try
        {
            var zip = await releases.DownloadVerifiedAsync(latest, $"ProcessTracker-{version}-x64.zip", downloadDir, ct);
            if (zip is null)
            {
                log.LogWarning("Downloading or verifying ProcessTracker {Version} failed; nothing changed", version);
                return UpdateCheck.DownloadFailed;
            }
            if (Extract(zip, target) is { } problem)
            {
                log.LogWarning("Extracting ProcessTracker {Version} to {Dir} failed: {Problem}; nothing started", version, target, problem);
                return UpdateCheck.ExtractFailed;
            }
        }
        finally
        {
            TryDelete(downloadDir);
        }

        var exe = Path.Combine(target, UserTask.ServiceExeName);
        try
        {
            env.Starter.Start(exe, ["--user", "--takeover", env.CurrentAppDir], target);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "Starting {Exe} failed; this version keeps running", exe);
            return UpdateCheck.StartFailed;
        }
        log.LogInformation("Started ProcessTracker {Version} from {Dir} to take over from {Current}", version, target, env.CurrentAppDir);
        return UpdateCheck.Started;
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with the zip's contents. Every entry is checked before anything is deleted or
    /// written: one that would land outside the target refuses the whole zip. Returns null on success, else what went
    /// wrong (after a failed extraction the target is gone).
    /// </summary>
    private static string? Extract(string zip, string target)
    {
        try
        {
            var root = AppFolders.Normalize(target) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return $"entry '{entry.FullName}' would be written outside the folder";
                }
            }
            AppFolders.Delete(target); // a leftover of the same version (an earlier, interrupted update) is replaced
            ZipFile.ExtractToDirectory(zip, target);
            if (!File.Exists(Path.Combine(target, UserTask.ServiceExeName)))
            {
                AppFolders.Delete(target);
                return $"the zip has no {UserTask.ServiceExeName}";
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or NotSupportedException)
        {
            TryDelete(target);
            return e.Message;
        }
    }

    private static void TryDelete(string dir)
    {
        try { AppFolders.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
