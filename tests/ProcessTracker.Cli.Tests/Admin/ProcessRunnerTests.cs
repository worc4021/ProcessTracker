using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Run_CapturesExitCodeAndBothStreams()
    {
        var (code, output) = new ProcessRunner().Run(Cmd, ["/c", "echo out& echo err 1>&2& exit 7"], TimeSpan.FromSeconds(30));
        Assert.Equal(7, code);
        Assert.Contains("out", output);
        Assert.Contains("err", output);
    }

    [Fact]
    public void Run_Timeout_KillsAndReturnsTimedOut()
    {
        var (code, output) = new ProcessRunner().Run(Cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(500));
        Assert.Equal(ProcessRunner.TimedOut, code);
        Assert.Contains("Timed out", output);
    }

    [Fact]
    public void Run_MissingProgram_ReturnsNotStarted()
    {
        var (code, _) = new ProcessRunner().Run(@"C:\definitely\missing.exe", [], TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessRunner.NotStarted, code);
    }
}
