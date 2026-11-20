using System.Text;
using System.Xml.Linq;

namespace ProcessTracker.Cli.Admin;

/// <summary>The SYSTEM scheduled task \ProcessTracker\AutoUpgrade that runs the admin-only upgrade.ps1 daily and at boot.</summary>
public sealed class UpgradeTask(IProcessRunner runner)
{
    public const string TaskName = @"\ProcessTracker\AutoUpgrade";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildXml(string installDir)
    {
        var script = Path.Combine(installDir, "upgrade.ps1");
        var powershell = Path.Combine("%SystemRoot%", @"System32\WindowsPowerShell\v1.0\powershell.exe");
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Upgrades ProcessTracker through winget (daily and shortly after boot).")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "CalendarTrigger",
                        new XElement(Ns + "StartBoundary", "2026-01-01T12:00:00"),
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "RandomDelay", "PT1H"),
                        new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", "1"))),
                    new XElement(Ns + "BootTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "Delay", "PT10M"))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", "S-1-5-18"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "true"),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT2H")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", powershell),
                        new XElement(Ns + "Arguments", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + doc.ToString();
    }

    public int Register(string installDir)
    {
        var file = Path.Combine(Path.GetTempPath(), $"proctrack-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(installDir), Encoding.Unicode); // schtasks wants UTF-16 (with BOM)
            return Run("/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    public int Remove() => Run("/Delete", "/TN", TaskName, "/F");

    public int SetEnabled(bool enabled) => Run("/Change", "/TN", TaskName, enabled ? "/ENABLE" : "/DISABLE");

    /// <summary>schtasks' verbose listing for the task; the text says so when it does not exist.</summary>
    public string Query()
    {
        var (code, output) = runner.Run("schtasks", ["/Query", "/TN", TaskName, "/V", "/FO", "LIST"], Timeout);
        return code == 0 ? output : $"The scheduled task {TaskName} is not registered (schtasks exit {code}).";
    }

    private int Run(params string[] args)
    {
        var (code, output) = runner.Run("schtasks", args, Timeout);
        if (code != 0) Console.Error.WriteLine(output.Trim());
        return code;
    }
}
