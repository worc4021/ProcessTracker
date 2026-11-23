using ProcessTracker.Native;

namespace ProcessTracker.Cli.Admin;

/// <summary>Runs an external program and returns its exit code and combined output (a seam so tests never start real tools).</summary>
public interface IProcessRunner
{
    (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>The real runner: <see cref="ToolRunner"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Exit code reported when the process had to be killed after the timeout.</summary>
    public const int TimedOut = ToolRunner.TimedOut;
    /// <summary>Exit code reported when the program could not be started at all.</summary>
    public const int NotStarted = ToolRunner.NotStarted;

    public (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout) => ToolRunner.Run(exe, args, timeout);
}
