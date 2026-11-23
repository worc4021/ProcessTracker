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
    /// <summary>Starts the exe detached from this console, in the given working directory.</summary>
    public required Action<string, string> StartDetached { get; init; }
    /// <summary>The health answer of the production port; null when nothing answers.</summary>
    public required Func<CancellationToken, Task<HealthDto?>> Health { get; init; }
    public required Func<bool> SignalExit { get; init; }
    public required string ProgramsDir { get; init; }
    public required string DataDir { get; init; }
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    public static string DefaultProgramsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProcessTracker");

    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker");
}

/// <summary>proctrack user install | uninstall | status: the per-user (no admin) ProcessTracker.</summary>
public sealed class UserInstall(UserInstallEnvironment env)
{
    public const int Ok = 0, Refused = 1;

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
        appDir = Path.GetFullPath(appDir);
        var exe = Path.Combine(appDir, UserTask.ServiceExeName);
        if (!File.Exists(exe))
        {
            env.Err.WriteLine($"{exe} not found. Nothing was changed.");
            return Refused;
        }

        if (env.Task.Register(appDir) != 0) return Refused;
        env.Out.WriteLine($"Registered {env.Task.TaskName}.");

        if (await HealthModeAsync(ct) != "user") env.StartDetached(exe, appDir);

        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await HealthModeAsync(ct) == "user")
            {
                env.Out.WriteLine("ProcessTracker user mode is running.");
                return Ok;
            }
            if (clock.Elapsed >= env.HealthTimeout) break;
            await Task.Delay(env.PollInterval, ct);
        }
        env.Err.WriteLine($"The task is registered, but ProcessTracker did not report mode=user within {env.HealthTimeout.TotalSeconds:0} s.");
        return Refused;
    }

    public async Task<int> UninstallAsync(bool purgeData, CancellationToken ct = default)
    {
        try { env.SignalExit(); }
        catch (UnauthorizedAccessException)
        {
            env.Err.WriteLine("A user-mode ProcessTracker of another account holds the exit signal; it was not stopped.");
        }
        // Give the instance a moment to release its files before they are deleted.
        var clock = Stopwatch.StartNew();
        while (await env.Health(ct) is not null && clock.Elapsed < env.StopTimeout)
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

    private async Task<string?> HealthModeAsync(CancellationToken ct) => (await env.Health(ct))?.Mode;

    private int DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            env.Err.WriteLine($"Could not delete {dir}: {e.Message}");
            return Refused;
        }
    }
}
