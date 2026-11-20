using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tests.Fakes;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Tests.Tracking;

public sealed class TrackerProfileTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ClaudePath = @"C:\Users\u\.local\bin\claude.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-trackerprof-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSnapshotSource _src = new(T0);
    private readonly FakeInspector _insp = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly List<Tracker> _trackers = new();

    private sealed class FakeResolver : IProfileResolver
    {
        public Dictionary<ProcessKey, string?> Map { get; } = new();
        public int Calls { get; private set; }

        public string? ProfileOf(ProcessKey key)
        {
            Calls++;
            return Map.GetValueOrDefault(key);
        }
    }

    public void Dispose()
    {
        foreach (var t in _trackers) t.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Tracker NewTracker(IProfileResolver? resolver, string userProfile = "")
    {
        var t = new Tracker(_src, _insp, new TrackerOptions { DataDir = Path.Combine(_dir, "data"), UserProfilePath = userProfile },
            NullLogger<Tracker>.Instance, _time, resolver);
        _trackers.Add(t);
        return t;
    }

    private string MakeProfile(string name, string title)
    {
        var profile = Path.Combine(_dir, name);
        var project = Path.Combine(profile, ".claude", "projects", "C--work");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "s1.jsonl"),
            """{"type":"user","cwd":"C:\\work","message":{"content":"TITLE"}}""".Replace("TITLE", title) + "\n");
        return profile;
    }

    private ProcessKey AddSession()
    {
        var anchor = _src.Add(10, 100, 1, "claude.exe");
        _insp.Set(anchor, FakeInspector.Ok(hostSessionId: "h1", imagePath: ClaudePath));
        var node = _src.Add(11, 200, 10, "node.exe");
        _insp.Set(node, FakeInspector.Ok(sessionId: "s1", hostSessionId: "h1"));
        return anchor;
    }

    [Fact]
    public void Tracker_UsesAnchorOwnerProfile_ForTranscripts()
    {
        var anchor = AddSession();
        var resolver = new FakeResolver();
        resolver.Map[anchor] = MakeProfile("owner", "from owner");

        var s = Assert.Single(NewTracker(resolver).Tick().Sessions);

        Assert.Equal("from owner", s.Info.Title);
    }

    [Fact]
    public void Tracker_ConfiguredProfile_WinsOverResolver()
    {
        var anchor = AddSession();
        var resolver = new FakeResolver();
        resolver.Map[anchor] = MakeProfile("owner", "from owner");
        var configured = MakeProfile("configured", "from config");

        var s = Assert.Single(NewTracker(resolver, configured).Tick().Sessions);

        Assert.Equal("from config", s.Info.Title);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Tracker_ResolverReturnsNull_NoTitleNoThrow()
    {
        AddSession();
        var s = Assert.Single(NewTracker(new FakeResolver()).Tick().Sessions);

        Assert.Null(s.Info.Title);
    }

    private const string DesktopHost = "local_86e91a8f-0885-4afb-8fbd-fe1b4050213b";

    private static string WriteDesktop(string profile, string title, string branch = "main")
    {
        var dir = Path.Combine(profile, "AppData", "Local", "Claude-Data", "claude_x", "claude-code-sessions", "a", "b");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DesktopHost + ".json");
        File.WriteAllText(path,
            $$"""{"sessionId":"{{DesktopHost}}","title":"{{title}}","branch":"{{branch}}","originCwd":"C:\\repos\\Widget","postTurnSummary":"did it"}""");
        return path;
    }

    [Fact]
    public void Tracker_DesktopSession_TakesDesktopTitle_RefreshedAtMostEvery60s()
    {
        var anchor = _src.Add(10, 100, 1, "claude.exe");
        _insp.Set(anchor, FakeInspector.Ok(hostSessionId: DesktopHost, imagePath: ClaudePath));
        var resolver = new FakeResolver();
        var profile = MakeProfile("owner", "first prompt");
        resolver.Map[anchor] = profile;
        var file = WriteDesktop(profile, "FSI module boundaries planning");
        var t = NewTracker(resolver);

        var s = Assert.Single(t.Tick().Sessions).Info;
        Assert.Equal("FSI module boundaries planning", s.DisplayTitle);
        Assert.Equal(TitleSources.Desktop, s.TitleSource);
        Assert.Equal("Widget", s.Project);
        Assert.Equal("main", s.Branch);
        Assert.Equal("did it", s.Summary);

        WriteDesktop(profile, "Renamed", branch: "feat/y");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        _src.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("FSI module boundaries planning", Assert.Single(t.Tick().Sessions).Info.DisplayTitle);

        _src.Advance(TimeSpan.FromSeconds(31));
        var after = Assert.Single(t.Tick().Sessions).Info;
        Assert.Equal("Renamed", after.DisplayTitle);
        Assert.Equal("feat/y", after.Branch);
    }

    [Fact]
    public void Tracker_DesktopTitle_SurvivesRestart()
    {
        var anchor = _src.Add(10, 100, 1, "claude.exe");
        _insp.Set(anchor, FakeInspector.Ok(hostSessionId: DesktopHost, imagePath: ClaudePath));
        var resolver = new FakeResolver();
        var profile = Path.Combine(_dir, "owner2");
        resolver.Map[anchor] = profile;
        WriteDesktop(profile, "Persisted title");
        var t = NewTracker(resolver);
        t.Tick();
        t.Dispose();

        resolver.Map[anchor] = null; // nothing readable after the restart
        Directory.Delete(profile, true);
        var t2 = NewTracker(resolver);
        t2.Restore();
        var s = Assert.Single(t2.Tick().Sessions).Info;
        Assert.Equal("Persisted title", s.DisplayTitle);
        Assert.Equal(TitleSources.Desktop, s.TitleSource);
    }

    [Fact]
    public void Tracker_NoDesktopFile_FallsBackToTranscriptTitle()
    {
        var anchor = _src.Add(10, 100, 1, "claude.exe");
        _insp.Set(anchor, FakeInspector.Ok(hostSessionId: DesktopHost, imagePath: ClaudePath));
        var node = _src.Add(11, 200, 10, "node.exe");
        _insp.Set(node, FakeInspector.Ok(sessionId: "s1", hostSessionId: DesktopHost));
        var resolver = new FakeResolver();
        resolver.Map[anchor] = MakeProfile("owner", "first prompt");

        var s = Assert.Single(NewTracker(resolver).Tick().Sessions).Info;
        Assert.Equal("first prompt", s.DisplayTitle);
        Assert.Equal(TitleSources.Transcript, s.TitleSource);
        Assert.Equal("work", s.Project);
    }

    [Fact]
    public void Tracker_ResolverLosesAnchor_KeepsLastResolvedProfile()
    {
        var anchor = AddSession();
        var resolver = new FakeResolver();
        resolver.Map[anchor] = MakeProfile("owner", "from owner");
        var t = NewTracker(resolver);
        t.Tick();

        resolver.Map[anchor] = null; // anchor exited
        File.AppendAllText(Path.Combine(_dir, "owner", ".claude", "projects", "C--work", "s1.jsonl"), "\n");
        _src.Advance(TimeSpan.FromSeconds(61));

        var s = Assert.Single(t.Tick().Sessions);
        Assert.Equal("from owner", s.Info.Title);
        Assert.NotNull(s.Info.LastTranscriptWriteUtc);
    }
}
