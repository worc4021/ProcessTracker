using System.CommandLine;
using ProcessTracker.Cli.Admin;
using ProcessTracker.Core.Updates;

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
        registerTask.SetAction(pr =>
        {
            var dir = InstallDirCheck.Normalize(pr.GetValue(installDir)!);
            if (RefuseUnlessElevated()) return CommandSupport.Refused;
            if (InstallDirCheck.Validate(dir) is { } problem)
            {
                Console.Error.WriteLine($"{problem} Nothing was changed.");
                return CommandSupport.Refused;
            }
            return RunTask(t => t.Register(dir), "registered");
        });

        var removeTask = new Command("remove-task", "Remove the auto-upgrade scheduled task (requires elevation).");
        removeTask.SetAction(_ => RunTask(t => t.Remove(), "removed"));

        var stopUserInstances = new Command("stop-user-instances",
            "Ask running user-mode instances to exit and wait up to 30 s for them (exit 1 on timeout).");
        stopUserInstances.SetAction(_ => Report(new StopUserInstances().Run()));

        var asset = new Option<string>("--asset") { Description = "Release asset to fetch.", Required = true };
        asset.AcceptOnlyFromAmong("msi");
        var outDir = new Option<string>("--out") { Description = "Folder to download into.", Required = true };
        var fetchRelease = new Command("fetch-release",
            "Download the latest release's MSI, hash-verified, if newer than this proctrack; print its path "
            + "(exit 0), 3 when up to date, 1 on failure.") { asset, outDir };
        fetchRelease.SetAction(async (pr, ct) =>
        {
            if (FetchRelease.InstalledVersion() is not { } installed)
            {
                Console.Error.WriteLine("This proctrack build has no X.Y.Z version.");
                return CommandSupport.Refused;
            }
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) };
            var releases = new ReleaseClient(handler, installed.ToString());
            return await new FetchRelease(releases, installed).RunAsync(pr.GetValue(asset)!, pr.GetValue(outDir)!, ct);
        });

        return new Command("admin", "Installer helpers.")
        {
            Hidden = true,
            Subcommands = { prepare, remove, registerTask, removeTask, stopUserInstances, fetchRelease },
        };
    }

    private static bool RefuseUnlessElevated()
    {
        if (DataFolderSetup.IsElevated()) return false;
        Console.Error.WriteLine("This command must run elevated. Nothing was changed.");
        return true;
    }

    private static int RunTask(Func<UpgradeTask, int> action, string doneWord)
    {
        if (RefuseUnlessElevated()) return CommandSupport.Refused;
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
