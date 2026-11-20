using ProcessTracker.Cli.Admin;
using ProcessTracker.Cli.Mcp;

namespace ProcessTracker.Cli.Tests.Mcp;

public class McpRegistrarTests
{
    private sealed class FakeRunner(Func<IReadOnlyList<string>, (int, string)> reply) : IProcessRunner
    {
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];
        public (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Add((exe, args));
            return reply(args);
        }
    }

    private const string Header = "X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}";

    [Fact]
    public void McpRegistrar_ClaudeMissing_ReturnsSkippedNotError()
    {
        var runner = new FakeRunner(_ => (0, ""));
        var registrar = new McpRegistrar(() => null, runner);
        Assert.Equal(McpResult.Skipped, registrar.Register());
        Assert.Equal(McpResult.Skipped, registrar.Unregister());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void McpRegistrar_Register_PassesHeaderLiterally()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (1, "not found") : (0, ""));
        var result = new McpRegistrar(() => @"C:\x\claude.exe", runner).Register();
        Assert.Equal(McpResult.Done, result);
        var add = runner.Calls.Single(c => c.Args[1] == "add");
        Assert.Equal(@"C:\x\claude.exe", add.Exe);
        Assert.Contains(Header, add.Args);
        Assert.Equal("user", add.Args[add.Args.ToList().IndexOf("--scope") + 1]);
        Assert.Equal("http", add.Args[add.Args.ToList().IndexOf("--transport") + 1]);
        Assert.Contains("process-tracker", add.Args);
        Assert.Contains("http://127.0.0.1:47811/mcp", add.Args);
    }

    [Fact]
    public void McpRegistrar_AlreadyRegistered_DoesNotAddAgain()
    {
        var runner = new FakeRunner(_ => (0, "process-tracker: ..."));
        Assert.Equal(McpResult.AlreadyInState, new McpRegistrar(() => "claude", runner).Register());
        Assert.DoesNotContain(runner.Calls, c => c.Args[1] == "add");
    }

    [Fact]
    public void McpRegistrar_Unregister_RunsRemove()
    {
        var runner = new FakeRunner(_ => (0, ""));
        Assert.Equal(McpResult.Done, new McpRegistrar(() => "claude", runner).Unregister());
        var remove = runner.Calls.Single(c => c.Args[1] == "remove");
        Assert.Equal(["mcp", "remove", "--scope", "user", "process-tracker"], remove.Args);
    }

    [Fact]
    public void McpRegistrar_Unregister_WhenNotRegistered_IsAlreadyInState()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (1, "") : (0, ""));
        Assert.Equal(McpResult.AlreadyInState, new McpRegistrar(() => "claude", runner).Unregister());
        Assert.DoesNotContain(runner.Calls, c => c.Args[1] == "remove");
    }

    [Fact]
    public void McpRegistrar_AddFails_Throws()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (1, "") : (3, "boom"));
        var e = Assert.Throws<InvalidOperationException>(() => new McpRegistrar(() => "claude", runner).Register());
        Assert.Contains("boom", e.Message);
    }
}
