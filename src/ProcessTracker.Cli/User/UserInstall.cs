using System.Diagnostics;
using ProcessTracker.Core.Contracts;
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
    /// <summary>%LOCALAPPDATA%; the only root anything is deleted under.</summary>
    public required string LocalAppData { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    public string ProgramsDir => Path.Combine(LocalAppData, "Programs", "ProcessTracker");
    public string DataDir => Path.Combine(LocalAppData, "ProcessTracker");

    public static string DefaultLocalAppData() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

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
        if (await RunningAsync(ct) == Running.Other)
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

        if (env.Task.Register(appDir) != 0) return Refused;
        env.Out.WriteLine($"Registered {env.Task.TaskName}.");

        // Task Scheduler starts it, as at logon: no handles inherited from this console, outside this process's job.
        if (await RunningAsync(ct) != Running.Mine && env.Task.Start() != 0) return Refused;

        var clock = Stopwatch.StartNew();
        while (true)
        {
            var running = await RunningAsync(ct);
            if (running == Running.Mine)
            {
                env.Out.WriteLine("ProcessTracker user mode is running.");
                return Ok;
            }
            if (running == Running.Other)
            {
                env.Err.WriteLine(OtherUser);
                return Refused;
            }
            if (clock.Elapsed >= env.HealthTimeout) break;
            await Task.Delay(env.PollInterval, ct);
        }
        env.Err.WriteLine($"The task is registered, but ProcessTracker did not report mode=user within {env.HealthTimeout.TotalSeconds:0} s.");
        return Refused;
    }

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
    private async Task<Running> RunningAsync(CancellationToken ct)
    {
        var health = await env.Health(ct);
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
