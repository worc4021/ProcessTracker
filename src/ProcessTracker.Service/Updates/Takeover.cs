using System.ComponentModel;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Native;

namespace ProcessTracker.Service.Updates;

/// <summary>Everything <see cref="Takeover"/> touches outside its own logic; tests replace all of it.</summary>
public sealed class TakeoverEnvironment
{
    /// <summary>This (the new) version's folder.</summary>
    public required string NewAppDir { get; init; }
    /// <summary>%LOCALAPPDATA%: the logon task is updated and old versions deleted only inside its install layout.</summary>
    public required string LocalAppData { get; init; }
    public required UserTask Task { get; init; }
    public required IProcessStarter Starter { get; init; }
    /// <summary>
    /// Stops this host and completes once the single-instance mutex is released, so the previous version, started
    /// afterwards, can take it (the mutex can only be released by the thread that acquired it: the program's main thread).
    /// </summary>
    public required Func<Task> ReleaseInstance { get; init; }
    /// <summary>True when this instance was asked to exit through the exit event (the MSI, or a newer version).</summary>
    public required Func<bool> ExitRequested { get; init; }
    /// <summary>Must keep working after the host is disposed (the rollback logs after that).</summary>
    public required ILogger Log { get; init; }
}

/// <summary>
/// The second half of a <c>--takeover &lt;previousAppDir&gt;</c> start, once the previous version has exited and the
/// mutex is ours. When the collector completes its first tick, and this version runs from the user-mode install, the
/// logon task is pointed at this version and the <c>app-*</c> folders other than this one and the previous one are
/// deleted. If that does not happen within
/// <see cref="HealthyWithin"/> (or the host fails to start), this instance stops, releases the mutex, starts the
/// previous version with <c>--user</c> and exits with <see cref="RolledBack"/>. The failed folder is marked so the
/// update worker does not try the same version again.
/// </summary>
public static class Takeover
{
    public static readonly TimeSpan HealthyWithin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Exit code of a takeover start that did not become healthy and started the previous version again.</summary>
    public const int RolledBack = 5;

    /// <summary>File written into a version's folder when its takeover was rolled back.</summary>
    public const string FailedMarkerName = "takeover-failed";

    /// <summary>Returns the process exit code: 0 once healthy (or when asked to exit), <see cref="RolledBack"/> otherwise.</summary>
    public static async Task<int> RunAsync(string previousAppDir, Func<Task<bool>> becameHealthy, TakeoverEnvironment env)
    {
        bool healthy;
        try { healthy = await becameHealthy(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            env.Log.LogError(e, "Waiting for this version to become healthy failed");
            healthy = false;
        }

        if (healthy)
        {
            env.Log.LogInformation("Took over from {Previous}: the collector completed its first tick", previousAppDir);
            Commit(previousAppDir, env);
            return 0;
        }

        if (env.ExitRequested())
        {
            // Starting the old version now would fight whoever asked us to exit (the MSI installing the machine service).
            env.Log.LogWarning("Asked to exit before this version became healthy; not starting {Previous} again", previousAppDir);
            return 0;
        }

        env.Log.LogError("This version did not complete a collector tick within {Seconds} s or its host failed; rolling back to {Previous}",
            HealthyWithin.TotalSeconds, previousAppDir);
        MarkFailed(env);
        await env.ReleaseInstance();

        var exe = Path.Combine(previousAppDir, UserTask.ServiceExeName);
        try
        {
            env.Starter.Start(exe, ["--user"], previousAppDir);
            env.Log.LogInformation("Started {Exe} --user", exe);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            env.Log.LogCritical(e, "Starting the previous version {Exe} failed; ProcessTracker is not running", exe);
        }
        return RolledBack;
    }

    /// <summary>
    /// True once the tracker has completed a tick (the same test as <c>/api/health</c>'s <c>lastTickUtc</c>); false when
    /// <paramref name="within"/> passes first or <paramref name="hostEnded"/> completes. <paramref name="host"/> is null
    /// until the host is built.
    /// </summary>
    public static async Task<bool> FirstTickAsync(Func<ITrackerHost?> host, Task hostEnded, TimeSpan within, TimeProvider time)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            if (host() is { } h && h.View.TimestampUtc != TrackerView.Empty.TimestampUtc) return true;
            if (hostEnded.IsCompleted) return false;
            var left = within - time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return false;
            await Task.WhenAny(hostEnded, Task.Delay(left < PollInterval ? left : PollInterval, time));
        }
    }

    private static void Commit(string previousAppDir, TakeoverEnvironment env)
    {
        // Only an install in %LOCALAPPDATA%\Programs\ProcessTracker\app-* owns the logon task and its siblings; a manual
        // run from a build folder must neither repoint the task nor delete anything.
        if (SelfUpdateWorker.InstallRootFor(env.NewAppDir, env.LocalAppData) is not { } installRoot)
        {
            env.Log.LogInformation("{Dir} is not an app folder of the user-mode install; the logon task and old versions are left alone",
                env.NewAppDir);
            return;
        }

        int code;
        try { code = env.Task.Register(env.NewAppDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            env.Log.LogError(e, "Registering the logon task for {Dir} failed", env.NewAppDir);
            code = -1;
        }
        if (code != 0)
        {
            // The task may still point at an older folder: keep them all.
            env.Log.LogWarning("The logon task was not updated (schtasks exit {Code}); keeping every app folder", code);
            return;
        }
        env.Log.LogInformation("The logon task {Task} now starts {Dir}", env.Task.TaskName, env.NewAppDir);
        try { Prune(installRoot, previousAppDir, env); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            env.Log.LogWarning("Listing the old version folders failed: {Error}", e.Message);
        }
    }

    /// <summary>Deletes the <c>app-*</c> folders in the install root, except this one and the previous one.</summary>
    private static void Prune(string root, string previousAppDir, TakeoverEnvironment env)
    {
        var current = AppFolders.Normalize(env.NewAppDir);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!AppFolders.IsAppFolderName(Path.GetFileName(dir))) continue;
            if (AppFolders.SamePath(dir, current) || AppFolders.SamePath(dir, previousAppDir)) continue;
            try
            {
                AppFolders.Delete(dir);
                env.Log.LogInformation("Deleted the old version folder {Dir}", dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                env.Log.LogWarning("Could not delete the old version folder {Dir}: {Error}", dir, e.Message);
            }
        }
    }

    private static void MarkFailed(TakeoverEnvironment env)
    {
        try
        {
            File.WriteAllText(Path.Combine(env.NewAppDir, FailedMarkerName),
                $"Rolled back at {DateTime.UtcNow:O}: no collector tick within {HealthyWithin.TotalSeconds} s.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            env.Log.LogWarning("Could not mark {Dir} as failed: {Error}", env.NewAppDir, e.Message);
        }
    }
}
