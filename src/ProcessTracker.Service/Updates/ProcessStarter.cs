using System.Diagnostics;

namespace ProcessTracker.Service.Updates;

/// <summary>Starts a program without waiting for it (a seam so tests never start real processes).</summary>
public interface IProcessStarter
{
    /// <summary>Starts <paramref name="exe"/> and returns at once. Throws when it cannot be started.</summary>
    void Start(string exe, IReadOnlyList<string> args, string workingDirectory);
}

/// <summary>
/// Starts a detached, windowless process: no shell, no window, and no share of our standard handles (they are
/// redirected to pipes whose ends we close at once; .NET's console ignores writes to a closed pipe). The working
/// directory is the new program's own folder, so this process's folder is never held open by it.
/// </summary>
public sealed class DetachedProcessStarter : IProcessStarter
{
    public void Start(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start.");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }
}
