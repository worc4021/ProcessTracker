using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using ProcessTracker.Native;

namespace ProcessTracker.Native.Tests;

public class UserTaskTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Text(XDocument doc, string path)
    {
        var mgr = new XmlNamespaceManager(new NameTable());
        mgr.AddNamespace("t", Ns.NamespaceName);
        return doc.XPathSelectElement(path, mgr)!.Value;
    }

    [Fact]
    public void BuildXml_UsesInteractiveTokenAndLeastPrivilege()
    {
        var doc = XDocument.Parse(UserTask.BuildXml(@"C:\Users\x\AppData\Local\Programs\ProcessTracker\app-1.2.3", Sid));

        Assert.Equal("InteractiveToken", Text(doc, "//t:Principal/t:LogonType"));
        Assert.Equal("LeastPrivilege", Text(doc, "//t:Principal/t:RunLevel"));
        Assert.Equal(Sid, Text(doc, "//t:LogonTrigger/t:UserId"));
        Assert.Equal(Sid, Text(doc, "//t:Principal/t:UserId"));
        Assert.EndsWith(@"\ProcessTracker.Service.exe", Text(doc, "//t:Exec/t:Command"));
        Assert.Equal("--user", Text(doc, "//t:Exec/t:Arguments"));
        Assert.Equal("IgnoreNew", Text(doc, "//t:Settings/t:MultipleInstancesPolicy"));
        Assert.Equal("false", Text(doc, "//t:Settings/t:DisallowStartIfOnBatteries"));
        Assert.Equal("PT0S", Text(doc, "//t:Settings/t:ExecutionTimeLimit"));
    }

    [Fact]
    public void BuildXml_EscapesPathWithAmpersand()
    {
        var doc = XDocument.Parse(UserTask.BuildXml(@"C:\R&D <tools>\app-1", Sid));

        Assert.Equal(@"C:\R&D <tools>\app-1\ProcessTracker.Service.exe", Text(doc, "//t:Exec/t:Command"));
    }

    [Fact]
    public void TaskName_IncludesSid() =>
        Assert.Equal($@"\ProcessTracker\User-{Sid}", new UserTask((_, _, _) => (0, ""), Sid).TaskName);

    [Fact]
    public void Register_WritesUtf16XmlAndForcesCreate()
    {
        string? args = null;
        byte[]? bytes = null;
        var task = new UserTask((exe, a, _) =>
        {
            args = string.Join(' ', a.Select(x => x.Contains(".xml") ? "<file>" : x));
            bytes = File.ReadAllBytes(a[a.ToList().IndexOf("/XML") + 1]);
            return (0, "");
        }, Sid);

        Assert.Equal(0, task.Register(@"C:\app"));

        Assert.Equal($@"/Create /TN \ProcessTracker\User-{Sid} /XML <file> /F", args);
        Assert.Equal([0xFF, 0xFE], bytes![..2]); // UTF-16 LE BOM
    }

    [Fact]
    public void RegisteredAppDir_ParsesActionPath()
    {
        var xml = UserTask.BuildXml(@"C:\R&D\app-1.2.3", Sid);
        var task = new UserTask((_, a, _) => (0, "\r\n" + xml), Sid);

        Assert.Equal(@"C:\R&D\app-1.2.3", task.RegisteredAppDir());
    }

    [Fact]
    public void RegisteredAppDir_NotRegistered_IsNull() =>
        Assert.Null(new UserTask((_, _, _) => (1, "ERROR: The system cannot find the file specified."), Sid).RegisteredAppDir());
}
