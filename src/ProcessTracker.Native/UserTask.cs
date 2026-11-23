using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ProcessTracker.Native;

/// <summary>
/// The per-user scheduled task <c>\ProcessTracker\User-&lt;SID&gt;</c> that starts user-mode ProcessTracker at logon.
/// The process runner is injected (the CLI passes its own; tests pass a fake) so this file starts nothing itself.
/// </summary>
public sealed class UserTask(Func<string, IReadOnlyList<string>, TimeSpan, (int ExitCode, string Output)> run, string? sid = null)
{
    public const string ServiceExeName = "ProcessTracker.Service.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _sid = sid ?? CurrentSid();

    /// <summary>Full path: a bare name would be searched in the current directory first.</summary>
    public static string SchtasksPath => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public string TaskName => $@"\ProcessTracker\User-{_sid}";

    public static string CurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current process has no user SID.");

    public static string BuildXml(string appDir, string sid)
    {
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Starts ProcessTracker in user mode when this user logs on.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", sid))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", sid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", Path.Combine(appDir, ServiceExeName)),
                        new XElement(Ns + "Arguments", "--user"),
                        new XElement(Ns + "WorkingDirectory", appDir)))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + doc.ToString();
    }

    public int Register(string appDir)
    {
        var file = Path.Combine(Path.GetTempPath(), $"proctrack-usertask-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(appDir, _sid), Encoding.Unicode); // schtasks wants UTF-16 (with BOM)
            return Run("/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    public int Remove() => Run("/Delete", "/TN", TaskName, "/F");

    /// <summary>The app folder the task starts from (its action path's directory); null when the task is not registered.</summary>
    public string? RegisteredAppDir()
    {
        var (code, output) = run(SchtasksPath, ["/Query", "/TN", TaskName, "/XML"], Timeout);
        if (code != 0) return null;
        var start = output.IndexOf("<Task", StringComparison.Ordinal);
        if (start < 0) return null;
        try
        {
            var command = XDocument.Parse(output[start..]).Descendants(Ns + "Exec").Elements(Ns + "Command").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(command) ? null : Path.GetDirectoryName(command.Trim('"'));
        }
        catch (XmlException) { return null; }
    }

    private int Run(params string[] args)
    {
        var (code, output) = run(SchtasksPath, args, Timeout);
        if (code != 0) Console.Error.WriteLine(output.Trim());
        return code;
    }
}
