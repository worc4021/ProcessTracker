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

    private static readonly string Nl = Environment.NewLine;
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
    public void McpCommand_ClaudeMissing_MessagePerAction()
    {
        Assert.Contains("run: proctrack mcp register", Commands.McpCommand.ClaudeMissingOnRegister);
        Assert.DoesNotContain("mcp register", Commands.McpCommand.ClaudeMissingOnUnregister);
        Assert.Contains("nothing changed", Commands.McpCommand.ClaudeMissingOnUnregister);
    }

    [Fact]
    public void McpRegistrar_Register_PassesHeaderLiterally()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (1, "not found") : (0, ""));
        var result = new McpRegistrar(() => @"C:\x\claude.exe", runner).Register();
        Assert.Equal(McpResult.Done, result);
        var add = runner.Calls.Single(c => c.Args[1] == "add");
        Assert.Equal(@"C:\x\claude.exe", add.Exe);
        Assert.Equal(["mcp", "add", "--transport", "http", "--scope", "user", "process-tracker", "http://127.0.0.1:47811/mcp", "--header", Header], add.Args);
    }

    [Fact]
    public void McpRegistrar_AlreadyRegistered_DoesNotAddAgain()
    {
        var runner = new FakeRunner(_ => (0, "process-tracker:" + Nl + "  Scope: User config (available in all your projects)"));
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

    [Fact]
    public void McpRegistrar_EntryInOtherScope_StillAddsUserEntry()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (0, "process-tracker:" + Nl + "  Scope: Local config") : (0, ""));
        Assert.Equal(McpResult.Done, new McpRegistrar(() => "claude", runner).Register());
        Assert.Contains(runner.Calls, c => c.Args[1] == "add");
    }

    [Fact]
    public void McpRegistrar_Unregister_EntryOnlyInOtherScope_IsAlreadyInState()
    {
        var runner = new FakeRunner(a => a[1] == "get" ? (0, "process-tracker:" + Nl + "  Scope: Project config") : (0, ""));
        Assert.Equal(McpResult.AlreadyInState, new McpRegistrar(() => "claude", runner).Unregister());
        Assert.DoesNotContain(runner.Calls, c => c.Args[1] == "remove");
    }
}
