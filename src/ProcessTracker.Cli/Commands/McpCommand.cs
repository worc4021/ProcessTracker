using System.CommandLine;
using ProcessTracker.Cli.Admin;
using ProcessTracker.Cli.Mcp;

namespace ProcessTracker.Cli.Commands;

/// <summary>proctrack mcp register|unregister: run as the user whose Claude Code config is to change.</summary>
public static class McpCommand
{
    public static Command Create()
    {
        var register = new Command("register", "Register the process-tracker MCP server with Claude Code (user scope).");
        register.SetAction(_ => Run(r => r.Register(), "registered", ClaudeMissingOnRegister));
        var unregister = new Command("unregister", "Remove the process-tracker MCP server from Claude Code (user scope).");
        unregister.SetAction(_ => Run(r => r.Unregister(), "removed", ClaudeMissingOnUnregister));
        return new Command("mcp", "Manage the Claude Code MCP registration (run as the user, not elevated).") { register, unregister };
    }

    public const string ClaudeMissingOnRegister =
        "claude was not found on the PATH or in %USERPROFILE%\\.local\\bin; nothing changed. Once Claude Code is installed, run: proctrack mcp register";

    public const string ClaudeMissingOnUnregister =
        "claude was not found on the PATH or in %USERPROFILE%\\.local\\bin, so there is no Claude Code registration to remove; nothing changed.";

    private static int Run(Func<McpRegistrar, McpResult> action, string doneWord, string claudeMissing)
    {
        try
        {
            switch (action(new McpRegistrar(McpRegistrar.LocateClaude, new ProcessRunner())))
            {
                case McpResult.Done: Console.WriteLine($"MCP server process-tracker {doneWord}."); break;
                case McpResult.AlreadyInState: Console.WriteLine($"Nothing to do: process-tracker is already in the requested state ({doneWord} or absent)."); break;
                case McpResult.Skipped:
                    Console.WriteLine(claudeMissing);
                    break;
            }
            return CommandSupport.Ok;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return CommandSupport.Refused;
        }
    }
}
