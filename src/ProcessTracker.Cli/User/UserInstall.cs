using System.Diagnostics;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Updates;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.User;

public sealed record UserStatus(
    bool TaskRegistered, string? AppDir, bool Responding, string? Mode, string? Version, string? Owner, bool MachineServiceInstalled);

/// <summary>Everything <see cref="UserInstall"/> touches outside its own logic; tests replace all of it.</summary>
public sealed class UserInstallEnvironment
{
    public required UserTask Task { get; init; }
    public required Func<bool> IsElevated { get; init; }
    public required Func<bool> MachineServiceInstalled { get; init; }
    /// <summary>The account this process runs as (DOMAIN\user); compared with the health answer's owner.</summary>
    public required Func<string> CurrentUser { get; init; }
    /// <summary>True when the running proctrack.exe lives inside the folder (it is locked while it runs).</summary>
    public required Func<string, bool> RunningFrom { get; init; }
    /// <summary>Starts a detached, windowless process (exe, command line, extra environment variables); used for the deferred folder cleanup.</summary>
    public required Action<string, string, IReadOnlyDictionary<string, string>> StartCleanup { get; init; }
    /// <summary>The health answer of the production port; null when nothing answers.</summary>
    public required Func<CancellationToken, Task<HealthDto?>> Health { get; init; }
    public required Func<bool> SignalExit { get; init; }
    /// <summary>
    /// Sets the exit event and waits up to the given time for the single-instance mutex to be free; false when it is
    /// still held. Used to restart an older (or relocated) copy of this user's instance.
    /// </summary>
    public required Func<TimeSpan, bool> StopInstance { get; init; }
    /// <summary>The X.Y.Z an app folder holds; null when unknown. Defaults to <see cref="DefaultAppVersion"/>.</summary>
    public Func<string, Version?> AppVersion { get; init; } = DefaultAppVersion;
    /// <summary>%LOCALAPPDATA%; the only root anything is deleted under.</summary>
    public required string LocalAppData { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long a stop waits for the instance: beyond user mode's own 15 s shutdown plus the final flush.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>While nothing answers after a start, the task is started again this often.</summary>
    public TimeSpan StartRetryInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    public string ProgramsDir => Path.Combine(LocalAppData, "Programs", "ProcessTracker");
    public string DataDir => Path.Combine(LocalAppData, "ProcessTracker");

    public static string DefaultLocalAppData() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The numeric X.Y.Z of <paramref name="appDir"/>'s service exe (its product version, the informational version),
    /// else of the folder name <c>app-X.Y.Z</c>; null when neither gives one.
    /// </summary>
    public static Version? DefaultAppVersion(string appDir)
    {
        try
        {
            var exe = Path.Combine(appDir, UserTask.ServiceExeName);
            if (File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).ProductVersion is { } product
                && ReleaseVersion.Numeric(product) is { } fromExe)
                return fromExe;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(appDir));
        return name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? ReleaseVersion.Numeric(name[4..]) : null;
    }

    public static bool DefaultRunningFrom(string dir) =>
        Environment.ProcessPath is { } exe
        && Path.GetFullPath(exe).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>proctrack user install | uninstall | status: the per-user (no admin) ProcessTracker.</summary>
public sealed class UserInstall(UserInstallEnvironment env)
{
    public const int Ok = 0, Refused = 1;
    private const string OtherUser = "this ProcessTracker belongs to another user; ask an admin to install the machine-wide service";

    private enum Running { None, Mine, Other }

    public async Task<int> InstallAsync(string appDir, CancellationToken ct = default)
    {
        if (env.IsElevated())
        {
            env.Err.WriteLine("run this from a normal, non-administrator terminal");
            return Refused;
        }
        if (env.MachineServiceInstalled())
        {
            env.Err.WriteLine("The machine-wide ProcessTracker service is installed; it serves every user, so user mode is not needed. Nothing was changed.");
            return Refused;
        }
        var before = await env.Health(ct);
        if (Classify(before) == Running.Other)
        {
            env.Err.WriteLine(OtherUser);
            return Refused;
        }
        appDir = Path.GetFullPath(appDir);
        var exe = Path.Combine(appDir, UserTask.ServiceExeName);
        if (!File.Exists(exe))
        {
            env.Err.WriteLine($"{exe} not found. Nothing was changed.");
            return Refused;
        }
        var target = env.AppVersion(appDir);

        // What to restore if the new version does not come up: the task's registration and, if one ran, the copy.
        var previousAppDir = env.Task.RegisteredAppDir();
        var wasRunning = Classify(before) == Running.Mine;

        // This user's instance already runs: keep it when it is this version from this folder; otherwise it is an older
        // (or relocated) copy that re-registering alone would leave running, so it is restarted below.
        var restart = wasRunning
                      && ((target is not null && ReleaseVersion.Numeric(before!.Version) != target)
                          || (previousAppDir is not null && !SamePath(previousAppDir, appDir)));

        if (env.Task.Register(appDir) != 0) return Refused;
        env.Out.WriteLine($"Registered {env.Task.TaskName}.");

        if (restart)
        {
            env.Out.WriteLine($"Restarting ProcessTracker {before!.Version} to run {Describe(target)} from {appDir}...");
            if (!env.StopInstance(env.StopTimeout))
            {
                env.Err.WriteLine($"The running ProcessTracker {before.Version} did not stop within {env.StopTimeout.TotalSeconds:0} s.");
                return await RollBackAsync(appDir, previousAppDir, before, stopNew: false, ct);
            }
        }

        // Task Scheduler starts it, as at logon: no handles inherited from this console, outside this process's job.
        if ((restart || Classify(await env.Health(ct)) != Running.Mine) && env.Task.Start() != 0)
        {
            env.Err.WriteLine("Starting the task failed.");
            return await RollBackAsync(appDir, previousAppDir, wasRunning ? before : null, stopNew: false, ct);
        }

        var clock = Stopwatch.StartNew();
        var lastStart = Stopwatch.StartNew();
        while (true)
        {
            var health = await env.Health(ct);
            var running = Classify(health);
            if (running == Running.None && lastStart.Elapsed >= env.StartRetryInterval)
            {
                // The task ignores /Run while it counts an instance as running (MultipleInstancesPolicy IgnoreNew); a
                // stopped instance may still be exiting when the first /Run came, so ask again while nothing answers.
                env.Task.Start();
                lastStart.Restart();
            }
            if (running == Running.Mine && (target is null || ReleaseVersion.Numeric(health!.Version) == target))
            {
                env.Out.WriteLine($"ProcessTracker {health!.Version} user mode is running.");
                return Ok;
            }
            if (running == Running.Other)
            {
                // Another account took the port while ours started: undo, and stop whatever of ours may still run.
                env.Err.WriteLine(OtherUser);
                return await RollBackAsync(appDir, previousAppDir, wasRunning ? before : null, stopNew: true, ct);
            }
            if (clock.Elapsed >= env.HealthTimeout) break;
            await Task.Delay(env.PollInterval, ct);
        }
        env.Err.WriteLine($"ProcessTracker {Describe(target)} did not start: it did not report mode=user within {env.HealthTimeout.TotalSeconds:0} s.");
        return await RollBackAsync(appDir, previousAppDir, wasRunning ? before : null, stopNew: true, ct);
    }

    /// <summary>
    /// After a failed (re)install: points the task back at <paramref name="previousAppDir"/> (or removes the task when
    /// there was none), and when a copy was running before (<paramref name="previouslyRunning"/>) starts it again and
    /// says whether it answers. <paramref name="stopNew"/> first stops whatever the failed start left running, so it
    /// releases the instance mutex and its folder; when that copy does not stop, the previous one could not start
    /// beside it, so it is left to the next logon. Always returns <see cref="Refused"/>.
    /// </summary>
    private async Task<int> RollBackAsync(string appDir, string? previousAppDir, HealthDto? previouslyRunning, bool stopNew,
        CancellationToken ct)
    {
        var stuck = stopNew && !env.StopInstance(env.StopTimeout);
        if (stuck) env.Err.WriteLine($"The new version did not stop within {env.StopTimeout.TotalSeconds:0} s either.");

        if (previousAppDir is null)
        {
            var removed = env.Task.Remove() == 0;
            env.Err.WriteLine(removed
                ? $"The task was removed again; nothing of {appDir} is registered."
                : $"Removing the task {env.Task.TaskName} failed; 'proctrack user uninstall' removes it.");
            return Refused;
        }
        if (!SamePath(previousAppDir, appDir) && env.Task.Register(previousAppDir) != 0)
        {
            env.Err.WriteLine($"Pointing the task back at {previousAppDir} failed; it still points at {appDir}.");
            return Refused;
        }
        if (previouslyRunning is null)
        {
            env.Err.WriteLine($"The task points at the previous version in {previousAppDir} again.");
            return Refused;
        }
        if (stuck)
        {
            env.Err.WriteLine($"The task points at the previous version {previouslyRunning.Version} again; it starts at your next logon, "
                              + "or when /process-tracker:install runs again after the stuck copy has exited.");
            return Refused;
        }

        // Only a copy started after this install began counts: the previous one may still be exiting after a stop that
        // timed out, and it answers with the same StartedUtc until it is gone. While nothing answers, the task is started
        // again (it ignores /Run while it still counts the exiting copy as running).
        env.Task.Start();
        var clock = Stopwatch.StartNew();
        var lastStart = Stopwatch.StartNew();
        while (true)
        {
            var health = await env.Health(ct);
            var running = Classify(health);
            if (running == Running.Mine && health!.StartedUtc != previouslyRunning.StartedUtc)
            {
                env.Err.WriteLine($"The new version did not start; the previous version {previouslyRunning.Version} is running again.");
                return Refused;
            }
            if (running == Running.Other)
            {
                env.Err.WriteLine($"The task points at the previous version {previouslyRunning.Version} again, but another user's ProcessTracker answers on the port.");
                return Refused;
            }
            if (running == Running.None && lastStart.Elapsed >= env.StartRetryInterval)
            {
                env.Task.Start();
                lastStart.Restart();
            }
            if (clock.Elapsed >= env.HealthTimeout) break;
            await Task.Delay(env.PollInterval, ct);
        }
        env.Err.WriteLine($"The new version did not start; the task points at the previous version {previouslyRunning.Version} again, "
                          + $"but no restarted copy answered within {env.HealthTimeout.TotalSeconds:0} s. /process-tracker:status shows details.");
        return Refused;
    }

    private static string Describe(Version? version) => version?.ToString(3) ?? "(version unknown)";

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    public async Task<int> UninstallAsync(bool purgeData, CancellationToken ct = default)
    {
        if (!DeletionRootsValid())
        {
            env.Err.WriteLine($"Refusing to delete: %LOCALAPPDATA% ('{env.LocalAppData}') is not a usable absolute folder. Nothing was changed.");
            return Refused;
        }

        try { env.SignalExit(); }
        catch (UnauthorizedAccessException)
        {
            env.Err.WriteLine("A user-mode ProcessTracker of another account holds the exit signal; it was not stopped.");
        }
        // Give the instance a moment to release its files before they are deleted.
        var clock = Stopwatch.StartNew();
        while (await RunningAsync(ct) == Running.Mine && clock.Elapsed < env.StopTimeout)
            await Task.Delay(env.PollInterval, ct);

        var code = Ok;
        if (env.Task.RegisteredAppDir() is not null && env.Task.Remove() != 0) code = Refused;
        code |= DeleteDir(env.ProgramsDir);
        if (purgeData) code |= DeleteDir(env.DataDir);
        else env.Out.WriteLine($"Kept {env.DataDir} (use --purge-data to delete it).");
        if (code == Ok) env.Out.WriteLine("ProcessTracker user mode uninstalled.");
        return code;
    }

    public async Task<UserStatus> StatusAsync(CancellationToken ct = default)
    {
        var appDir = env.Task.RegisteredAppDir();
        var health = await env.Health(ct);
        return new UserStatus(appDir is not null, appDir, health is not null, health?.Mode, health?.Version, health?.Owner,
            env.MachineServiceInstalled());
    }

    /// <summary>Name of the environment variable that carries the folder to the cleanup helper (never part of the script text).</summary>
    public const string CleanupDirVariable = "PROCTRACK_CLEANUP_DIR";

    /// <summary>The script text, decoded from <see cref="CleanupCommandLine"/>; it contains only the PID and the variable name.</summary>
    public static string CleanupScript(int pid) =>
        $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; "
        + $"Remove-Item -LiteralPath $env:{CleanupDirVariable} -Recurse -Force -ErrorAction SilentlyContinue";

    /// <summary>PowerShell arguments that wait for <paramref name="pid"/> to exit, then remove the folder named by the environment variable.</summary>
    public static string CleanupCommandLine(int pid) =>
        "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
        + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(CleanupScript(pid)));

    public static string CleanupExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Whether a user-mode instance answers on the port, and whether it is this user's.</summary>
    private async Task<Running> RunningAsync(CancellationToken ct) => Classify(await env.Health(ct));

    private Running Classify(HealthDto? health)
    {
        if (health is not { Mode: "user" }) return Running.None;
        return string.Equals(health.Owner, env.CurrentUser(), StringComparison.OrdinalIgnoreCase) ? Running.Mine : Running.Other;
    }

    private bool DeletionRootsValid()
    {
        var root = env.LocalAppData;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        if (Path.GetPathRoot(root) is { } r && Path.TrimEndingDirectorySeparator(r) == Path.TrimEndingDirectorySeparator(root)) return false;
        return Path.IsPathFullyQualified(env.ProgramsDir) && Path.IsPathFullyQualified(env.DataDir);
    }

    private int DeleteDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return Ok;
            if (!env.RunningFrom(dir))
            {
                Directory.Delete(dir, recursive: true);
                return Ok;
            }
            // proctrack.exe itself is in there and locked: remove what can go now, let a detached helper finish.
            DeleteBestEffort(dir);
            env.StartCleanup(CleanupExe, CleanupCommandLine(env.ProcessId), new Dictionary<string, string> { [CleanupDirVariable] = dir });
            env.Out.WriteLine($"Removal of {dir} finishes when this command exits.");
            return Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            env.Err.WriteLine($"Could not delete {dir}: {e.Message}");
            return Refused;
        }
    }

    private static void DeleteBestEffort(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var sub in Directory.EnumerateDirectories(dir))
            DeleteBestEffort(sub);
        try { Directory.Delete(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
