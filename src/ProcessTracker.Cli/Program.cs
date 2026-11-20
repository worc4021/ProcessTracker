using System.CommandLine;
using ProcessTracker.Cli.Commands;

namespace ProcessTracker.Cli;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("ProcessTracker command line client. Exit codes: 0 ok, 1 refused/failed, 2 service unreachable.")
        {
            CommandSupport.DevOption,
            LoadCommand.Create(),
            SessionsCommand.Create(),
            OrphansCommand.Create(),
            TreeCommand.Create(),
            HistoryCommand.Create(),
            KillCommand.Create(),
            McpCommand.Create(),
            UpgradeTaskCommand.Create(),
            AdminCommand.Create(),
        };
        return root.Parse(args).InvokeAsync();
    }
}
