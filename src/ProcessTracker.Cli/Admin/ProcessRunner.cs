using System.Diagnostics;
using System.Text;

namespace ProcessTracker.Cli.Admin;

/// <summary>Runs an external program and returns its exit code and combined output (a seam so tests never start real tools).</summary>
public interface IProcessRunner
{
    (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout);
}

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Exit code reported when the process had to be killed after the timeout.</summary>
    public const int TimedOut = -1;

    public (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (gate) output.AppendLine(line);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.WaitForExit();
                Append($"Timed out after {timeout.TotalSeconds:0} s and was killed.");
                lock (gate) return (TimedOut, output.ToString());
            }

            process.WaitForExit(); // flush the async output readers
            lock (gate) return (process.ExitCode, output.ToString());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (TimedOut, $"Could not start {exe}: {e.Message}");
        }
    }
}
