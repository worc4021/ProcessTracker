using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using ProcessTracker.Cli.Admin;
using ProcessTracker.Cli.User;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Commands;

/// <summary>proctrack user install|uninstall|status: ProcessTracker for one user, without administrator rights.</summary>
public static class UserCommand
{
    public static Command Create()
    {
        var appDir = new Option<string>("--app-dir") { Description = "Folder holding ProcessTracker.Service.exe.", Required = true };
        var install = new Command("install", "Register the logon task and start ProcessTracker for this user (not elevated).") { appDir };
        install.SetAction((pr, ct) => Build().InstallAsync(pr.GetValue(appDir)!, ct));

        var purge = new Option<bool>("--purge-data") { Description = @"Also delete %LOCALAPPDATA%\ProcessTracker." };
        var uninstall = new Command("uninstall", "Stop ProcessTracker, remove the logon task and the program files.") { purge };
        uninstall.SetAction((pr, ct) => Build().UninstallAsync(pr.GetValue(purge), ct));

        var json = CommandSupport.JsonOption();
        var status = new Command("status", "Show the logon task, whether the service answers, and the machine service.") { json };
        status.SetAction(async (pr, ct) =>
        {
            var s = await Build().StatusAsync(ct);
            if (pr.GetValue(json))
                Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            else
            {
                Console.WriteLine($"Logon task:      {(s.TaskRegistered ? $"registered ({s.AppDir})" : "not registered")}");
                var owner = s.Owner is null ? "" : $", owner {s.Owner}";
                Console.WriteLine($"Service answers: {(s.Responding ? $"yes (mode {s.Mode}, version {s.Version}{owner})" : "no")}");
                Console.WriteLine($"Machine service: {(s.MachineServiceInstalled ? "installed" : "not installed")}");
            }
            return CommandSupport.Ok;
        });

        return new Command("user", "ProcessTracker for the current user (no administrator rights needed).") { install, uninstall, status };
    }

    private static UserInstall Build() => new(new UserInstallEnvironment
    {
        Task = new UserTask(new ProcessRunner().Run),
        IsElevated = DataFolderSetup.IsElevated,
        MachineServiceInstalled = ServiceProbe.IsMachineServiceInstalled,
        StartDetached = (exe, dir) =>
            Process.Start(new ProcessStartInfo(exe, "--user") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })?.Dispose(),
        Health = async ct =>
        {
            using var client = new ApiClient(ApiClient.ResolveBaseUrl(dev: false));
            try { return await client.HealthAsync(ct); }
            catch (Exception e) when (e is ServiceUnreachableException or ApiException) { return null; }
        },
        SignalExit = () => UserModeSignal.TrySignal(),
        ProgramsDir = UserInstallEnvironment.DefaultProgramsDir(),
        DataDir = UserInstallEnvironment.DefaultDataDir(),
    });
}
