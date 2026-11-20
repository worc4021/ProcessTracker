using System.CommandLine;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Commands;

/// <summary>proctrack upgrade-task enable|disable|status: the user-facing switch for the daily winget upgrade task.</summary>
public static class UpgradeTaskCommand
{
    public static Command Create()
    {
        var enable = new Command("enable", "Enable the automatic upgrade task (requires elevation).");
        enable.SetAction(_ => Change(true));
        var disable = new Command("disable", "Disable the automatic upgrade task (requires elevation).");
        disable.SetAction(_ => Change(false));
        var status = new Command("status", "Show the automatic upgrade task.");
        status.SetAction(_ => { Console.WriteLine(new UpgradeTask(new ProcessRunner()).Query()); return CommandSupport.Ok; });
        return new Command("upgrade-task", "Control the daily automatic upgrade via winget.") { enable, disable, status };
    }

    private static int Change(bool enabled)
    {
        if (!DataFolderSetup.IsElevated())
        {
            Console.Error.WriteLine($"upgrade-task {(enabled ? "enable" : "disable")} must run elevated (from an administrator terminal). Nothing was changed.");
            return CommandSupport.Refused;
        }
        var code = new UpgradeTask(new ProcessRunner()).SetEnabled(enabled);
        if (code != 0) return CommandSupport.Refused;
        Console.WriteLine($"Automatic upgrade task {(enabled ? "enabled" : "disabled")}.");
        return CommandSupport.Ok;
    }
}
