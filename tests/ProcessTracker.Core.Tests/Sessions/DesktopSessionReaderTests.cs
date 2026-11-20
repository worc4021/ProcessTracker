using System.Diagnostics;
using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tests.Sessions;

public sealed class DesktopSessionReaderTests : IDisposable
{
    private const string HostId = "local_86e91a8f-0885-4afb-8fbd-fe1b4050213b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-desk-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "profile");

    public void Dispose()
    {
        // Delete junctions first without following them (Directory.Delete on a junction removes only the link).
        foreach (var link in _links) try { Directory.Delete(link); } catch (IOException) { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    private readonly List<string> _links = new();

    private string SessionsDir(string instance = "claude_x") =>
        Path.Combine(Profile, "AppData", "Local", "Claude-Data", instance, "claude-code-sessions");

    private string WriteSession(string json, string id = HostId, string account = "a", string org = "b", string instance = "claude_x")
    {
        var dir = Path.Combine(SessionsDir(instance), account, org);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        _links.Add(link);
        Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private const string Full = """
        {"sessionId":"local_86e91a8f-0885-4afb-8fbd-fe1b4050213b","cliSessionId":"c1","cwd":"C:\\repos\\X\\.claude\\worktrees\\wt",
         "originCwd":"C:\\repos\\X","worktreeName":"wt","branch":"feat/names","title":"FSI module boundaries planning",
         "titleSource":"auto","postTurnSummary":"Planned the split.","lastFocusedAt":1795212314441,
         "enabledMcpTools":{"a":true},"gitAnchors":[{"gitRoot":"C:\\repos\\X"}]}
        """;

    [Fact]
    public void Read_FindsFixtureTree_AndExtractsFields()
    {
        WriteSession(Full);
        var d = new DesktopSessionReader(Profile).Read(HostId);
        Assert.NotNull(d);
        Assert.Equal("FSI module boundaries planning", d.Title);
        Assert.Equal("feat/names", d.Branch);
        Assert.Equal("wt", d.WorktreeName);
        Assert.Equal(@"C:\repos\X\.claude\worktrees\wt", d.Cwd);
        Assert.Equal(@"C:\repos\X", d.OriginCwd);
        Assert.Equal("Planned the split.", d.Summary);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795212314441).UtcDateTime, d.LastFocusedUtc);
    }

    [Fact]
    public void Read_MissingOptionalFields_AreNull_AndOtherFilesIgnored()
    {
        WriteSession("""{"sessionId":"x","title":"Only a title","branch":""}""");
        var dir = Path.Combine(SessionsDir(), "a", "b");
        File.WriteAllText(Path.Combine(dir, "archived-sessions.idx"), "junk");
        File.WriteAllText(Path.Combine(dir, "scheduled-tasks.json"), "{}");
        var d = new DesktopSessionReader(Profile).Read(HostId)!;
        Assert.Equal("Only a title", d.Title);
        Assert.Null(d.Branch);
        Assert.Null(d.Summary);
        Assert.Null(d.LastFocusedUtc);
    }

    [Fact]
    public void Read_SearchesEveryInstanceAccountAndOrg()
    {
        WriteSession("""{"title":"other"}""", id: "local_00000000-0000-0000-0000-000000000000");
        Directory.CreateDirectory(Path.Combine(SessionsDir("claude_y"), "acc1", "org1"));
        WriteSession("""{"title":"found"}""", account: "acc2", org: "org9", instance: "claude_y");
        Assert.Equal("found", new DesktopSessionReader(Profile).Read(HostId)!.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local_86e91a8f")]
    [InlineData("local_..\\..\\..\\..\\..\\x0885-4afb-8fbd-fe1b4050213b")]
    [InlineData("local_86e91a8f-0885-4afb-8fbd-fe1b4050213b\n")]
    [InlineData("LOCAL_86e91a8f-0885-4afb-8fbd-fe1b4050213b")]
    [InlineData("local_86e91a8f-0885-4afb-8fbd-fe1b4050213z")]
    [InlineData("86e91a8f-0885-4afb-8fbd-fe1b4050213b")]
    public void Read_MalformedHostId_Null(string? id)
    {
        WriteSession(Full);
        Assert.False(DesktopSessionReader.IsValidHostId(id));
        Assert.Null(new DesktopSessionReader(Profile).Read(id));
    }

    [Fact]
    public void Read_JunctionedOrgDirectory_IsNotFollowed()
    {
        // The matching file lives outside the tree; the tree reaches it only through a junction.
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, HostId + ".json"), Full);
        Junction(Path.Combine(SessionsDir(), "a", "b"), elsewhere);

        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_JunctionedInstanceOrSessionsDirectory_IsNotFollowed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere-tree");
        var inner = Path.Combine(elsewhere, "claude-code-sessions", "a", "b");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, HostId + ".json"), Full);
        Junction(Path.Combine(Profile, "AppData", "Local", "Claude-Data", "claude_j"), elsewhere);
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));

        Junction(SessionsDir("claude_k"), Path.Combine(elsewhere, "claude-code-sessions"));
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_JunctionedClaudeDataRoot_IsNotFollowed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere-root");
        var inner = Path.Combine(elsewhere, "claude_x", "claude-code-sessions", "a", "b");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, HostId + ".json"), Full);
        Junction(Path.Combine(Profile, "AppData", "Local", "Claude-Data"), elsewhere);
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_CachedPath_LaterReplacedByJunction_IsNotFollowed()
    {
        WriteSession(Full);
        var reader = new DesktopSessionReader(Profile);
        Assert.NotNull(reader.Read(HostId));

        var elsewhere = Path.Combine(_root, "elsewhere-swap");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, HostId + ".json"), """{"title":"swapped"}""");
        Directory.Delete(Path.Combine(SessionsDir(), "a", "b"), true);
        Junction(Path.Combine(SessionsDir(), "a", "b"), elsewhere);

        Assert.Null(reader.Read(HostId));
    }

    [Fact]
    public void Read_TruncatesLongFields_AndCollapsesWhitespaceAndControls()
    {
        var title = "  Line one\n\tline\u0007 two\u202E " + new string('t', 300);
        var summary = new string('s', 1000);
        var path = @"C:\" + new string('p', 1000);
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["title"] = title, ["postTurnSummary"] = summary, ["cwd"] = path, ["originCwd"] = path,
            ["branch"] = new string('b', 500),
        });
        WriteSession(json);
        var d = new DesktopSessionReader(Profile).Read(HostId)!;
        Assert.Equal(DesktopSessionReader.MaxTitle, d.Title!.Length);
        Assert.StartsWith("Line one line two ttt", d.Title);
        Assert.Equal(DesktopSessionReader.MaxSummary, d.Summary!.Length);
        Assert.Equal(DesktopSessionReader.MaxPath, d.Cwd!.Length);
        Assert.Equal(DesktopSessionReader.MaxPath, d.OriginCwd!.Length);
        Assert.Equal(DesktopSessionReader.MaxName, d.Branch!.Length);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"title\":")]
    [InlineData("")]
    public void Read_BadJson_Null(string content)
    {
        WriteSession(content);
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_FileOverOneMegabyte_Null()
    {
        WriteSession("{\"title\":\"big\",\"pad\":\"" + new string('x', DesktopSessionReader.MaxBytes) + "\"}");
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_NoTree_Null()
    {
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
        Directory.CreateDirectory(Profile);
        Assert.Null(new DesktopSessionReader(Profile).Read(HostId));
    }

    [Fact]
    public void Read_CachesUntilTheFileChanges()
    {
        var path = WriteSession("""{"title":"first"}""");
        var reader = new DesktopSessionReader(Profile);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, t0);
        Assert.Equal("first", reader.Read(HostId)!.Title);

        // Same timestamp: the cached result is returned without re-parsing.
        File.WriteAllText(path, """{"title":"second"}""");
        File.SetLastWriteTimeUtc(path, t0);
        Assert.Equal("first", reader.Read(HostId)!.Title);

        File.SetLastWriteTimeUtc(path, t0.AddSeconds(1));
        Assert.Equal("second", reader.Read(HostId)!.Title);
    }

    [Fact]
    public void Read_LockedForWrite_StillReadable()
    {
        var path = WriteSession(Full);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("FSI module boundaries planning", new DesktopSessionReader(Profile).Read(HostId)!.Title);
    }

    [Theory]
    [InlineData(null, 10, null)]
    [InlineData("   ", 10, null)]
    [InlineData(" a \r\n b ", 10, "a b")]
    [InlineData("abcdefghijkl", 5, "abcde")]
    public void Clean_Cases(string? input, int max, string? expected) =>
        Assert.Equal(expected, DesktopSessionReader.Clean(input, max));

    [Fact]
    public void Clean_DoesNotSplitSurrogatePair()
    {
        var s = DesktopSessionReader.Clean("abcd😀efg", 5)!;
        Assert.Equal("abcd", s);
    }
}
