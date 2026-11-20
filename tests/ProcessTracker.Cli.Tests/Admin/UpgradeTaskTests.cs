using System.Xml.Linq;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public class UpgradeTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string InstallDir = @"C:\Program Files\ProcessTracker";

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];
        public string? XmlSeen;
        public byte[]? XmlBytes;
        public (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Add((exe, args));
            var i = args.ToList().IndexOf("/XML");
            if (i >= 0)
            {
                XmlBytes = File.ReadAllBytes(args[i + 1]);
                XmlSeen = File.ReadAllText(args[i + 1]);
            }
            return (0, "ok");
        }
    }

    [Fact]
    public void UpgradeTask_Xml_RunsAsSystemWithPinnedPowershellAndScript()
    {
        var doc = XDocument.Parse(UpgradeTask.BuildXml(InstallDir));
        var task = doc.Root!;
        Assert.Equal("1.4", task.Attribute("version")!.Value);
        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("S-1-5-18", principal.Element(Ns + "UserId")!.Value);
        Assert.Null(principal.Element(Ns + "LogonType")); // schtasks rejects it for the SYSTEM principal
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);

        var exec = task.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", exec.Element(Ns + "Command")!.Value);
        var args = exec.Element(Ns + "Arguments")!.Value;
        Assert.Contains("-NoProfile -NonInteractive -ExecutionPolicy Bypass", args);
        Assert.Contains(@"-File ""C:\Program Files\ProcessTracker\upgrade.ps1""", args);

        var triggers = task.Element(Ns + "Triggers")!;
        var cal = triggers.Element(Ns + "CalendarTrigger")!;
        Assert.EndsWith("T12:00:00", cal.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("PT1H", cal.Element(Ns + "RandomDelay")!.Value);
        Assert.Equal("1", cal.Element(Ns + "ScheduleByDay")!.Element(Ns + "DaysInterval")!.Value);
        Assert.Equal("PT10M", triggers.Element(Ns + "BootTrigger")!.Element(Ns + "Delay")!.Value);

        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.Equal("PT2H", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
    }

    [Fact]
    public void UpgradeTask_Xml_EscapesInstallDir()
    {
        var doc = XDocument.Parse(UpgradeTask.BuildXml(@"C:\A & B\<x>"));
        Assert.Contains(@"C:\A & B\<x>\upgrade.ps1", doc.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void UpgradeTask_Register_WritesUtf16XmlAndRunsSchtasks()
    {
        var runner = new FakeRunner();
        Assert.Equal(0, new UpgradeTask(runner).Register(InstallDir));
        var call = runner.Calls.Single();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), call.Exe);
        Assert.True(Path.IsPathFullyQualified(call.Exe));
        Assert.Equal(["/Create", "/TN", @"\ProcessTracker\AutoUpgrade", "/XML"], call.Args.Take(4));
        Assert.Equal("/F", call.Args[^1]);
        Assert.Contains("S-1-5-18", runner.XmlSeen);
        Assert.Equal([0xFF, 0xFE], runner.XmlBytes!.Take(2)); // UTF-16 LE byte order mark
        Assert.False(File.Exists(call.Args[4]));
    }

    [Fact]
    public void UpgradeTask_RemoveEnableDisableQuery_UseExpectedSchtasksArguments()
    {
        var runner = new FakeRunner();
        var t = new UpgradeTask(runner);
        t.Remove(); t.SetEnabled(true); t.SetEnabled(false); t.Query();
        Assert.Equal(["/Delete", "/TN", @"\ProcessTracker\AutoUpgrade", "/F"], runner.Calls[0].Args);
        Assert.Equal(["/Change", "/TN", @"\ProcessTracker\AutoUpgrade", "/ENABLE"], runner.Calls[1].Args);
        Assert.Equal(["/Change", "/TN", @"\ProcessTracker\AutoUpgrade", "/DISABLE"], runner.Calls[2].Args);
        Assert.Equal("/Query", runner.Calls[3].Args[0]);
        Assert.All(runner.Calls, c => Assert.Equal(UpgradeTask.SchtasksPath, c.Exe));
    }
}
