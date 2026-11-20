using System.CommandLine;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Commands;

/// <summary>Installer-only subcommands (run by the MSI as SYSTEM); hidden from --help.</summary>
public static class AdminCommand
{
    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker");

    public static Command Create()
    {
        var dataDir = new Option<string>("--data-dir")
        {
            Description = @"Data folder (default %ProgramData%\ProcessTracker).",
            DefaultValueFactory = _ => DefaultDataDir(),
        };
        var purge = new Option<bool>("--purge") { Description = "Delete the data folder." };

        var prepare = new Command("prepare-data", "Create and secure the data folder (requires elevation).") { dataDir };
        prepare.SetAction(pr => Report(new DataFolderSetup(pr.GetValue(dataDir)!).Prepare()));

        var remove = new Command("remove-data", "Remove the data folder when --purge is given.") { dataDir, purge };
        remove.SetAction(pr => Report(new DataFolderSetup(pr.GetValue(dataDir)!).Remove(pr.GetValue(purge))));

        var installDir = new Option<string>("--install-dir") { Description = "Install folder holding upgrade.ps1.", Required = true };
        var registerTask = new Command("register-task", "Register the SYSTEM auto-upgrade scheduled task (requires elevation).") { installDir };
        registerTask.SetAction(pr => RunTask(t => t.Register(pr.GetValue(installDir)!), "registered"));

        var removeTask = new Command("remove-task", "Remove the auto-upgrade scheduled task (requires elevation).");
        removeTask.SetAction(_ => RunTask(t => t.Remove(), "removed"));

        return new Command("admin", "Installer helpers.") { Hidden = true, Subcommands = { prepare, remove, registerTask, removeTask } };
    }

    private static int RunTask(Func<UpgradeTask, int> action, string doneWord)
    {
        if (!DataFolderSetup.IsElevated())
        {
            Console.Error.WriteLine("This command must run elevated. Nothing was changed.");
            return CommandSupport.Refused;
        }
        if (action(new UpgradeTask(new ProcessRunner())) != 0) return CommandSupport.Refused;
        Console.WriteLine($"Scheduled task {UpgradeTask.TaskName} {doneWord}.");
        return CommandSupport.Ok;
    }

    private static int Report(SetupResult result)
    {
        if (result.Ok) { Console.WriteLine(result.Message); return CommandSupport.Ok; }
        Console.Error.WriteLine(result.Message);
        return CommandSupport.Refused;
    }
}
