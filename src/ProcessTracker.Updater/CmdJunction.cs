using System.Diagnostics;

namespace ProcessTracker.Updater;

/// <summary>
/// Directory junctions through cmd.exe: <c>rmdir</c> removes a junction itself (never the target's contents), then
/// <c>mklink /J</c> creates the new one.
/// </summary>
public sealed class CmdJunction : IJunction
{
    public string? Target(string link) => new DirectoryInfo(link).LinkTarget;

    public void Point(string link, string target)
    {
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Junction target {target} does not exist.");
        var existing = new DirectoryInfo(link);
        if (existing.LinkTarget is not null) Cmd($"rmdir \"{link}\"");
        else if (existing.Exists) throw new IOException($"{link} is a real directory, not a junction; refusing to replace it.");
        Cmd($"mklink /J \"{link}\" \"{target}\"");
    }

    /// <summary>Runs one cmd.exe command (AutoRun disabled) and throws if it fails.</summary>
    private static void Cmd(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new IOException("cmd.exe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"cmd /c {command} failed with exit code {process.ExitCode}: {(stderr + stdout.Result).Trim()}");
    }
}
