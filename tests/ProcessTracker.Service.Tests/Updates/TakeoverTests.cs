using System.Collections.Frozen;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Native;
using ProcessTracker.Service.Tests.Fakes;
using ProcessTracker.Service.Updates;

namespace ProcessTracker.Service.Tests.Updates;

/// <summary>All with fakes: a recording schtasks runner, a recording starter and temporary folders.</summary>
public sealed class TakeoverTests : IDisposable
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private readonly TempDir _temp = new();
    private readonly FakeStarter _starter = new();
    private readonly List<string> _events = [];
    private readonly List<IReadOnlyList<string>> _schtasks = [];
    private string? _registeredCommand;
    private bool _exitRequested;

    private string Root => Path.Combine(_temp.Path, "Programs", "ProcessTracker");
    private string Previous => Path.Combine(Root, "app-0.2.0");
    private string New => Path.Combine(Root, "app-0.3.0");

    public TakeoverTests()
    {
        foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0" }) Directory.CreateDirectory(Path.Combine(Root, "app-" + v));
        _starter.OnStart = () => _events.Add("start");
    }

    public void Dispose() => _temp.Dispose();

    private (int, string) Schtasks(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Assert.Equal(UserTask.SchtasksPath, exe);
        _schtasks.Add(args);
        var xml = args.SkipWhile(a => a != "/XML").Skip(1).FirstOrDefault();
        if (args[0] == "/Create" && xml is not null)
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            _registeredCommand = XDocument.Load(xml).Descendants(ns + "Command").Single().Value;
        }
        return (0, "");
    }

    private TakeoverEnvironment Env(string? newAppDir = null) => new()
    {
        NewAppDir = newAppDir ?? New,
        LocalAppData = _temp.Path,
        Task = new UserTask(Schtasks, Sid),
        Starter = _starter,
        ReleaseInstance = () => { _events.Add("release"); return Task.CompletedTask; },
        ExitRequested = () => _exitRequested,
        Log = NullLogger.Instance,
    };

    private static IEnumerable<string> AppFolders(string root) =>
        Directory.GetDirectories(root).Select(Path.GetFileName).Order()!;

    [Fact]
    public async Task Takeover_Healthy_RegistersTaskAndPrunes()
    {
        Directory.CreateDirectory(Path.Combine(Root, "not-an-app"));
        File.WriteAllText(Path.Combine(Root, "app-file.txt"), "a file, not a folder");

        var code = await Takeover.RunAsync(Previous, () => Task.FromResult(true), Env());

        Assert.Equal(0, code);
        Assert.Equal("/Create", Assert.Single(_schtasks)[0]);
        Assert.Equal(Path.Combine(New, UserTask.ServiceExeName), _registeredCommand);
        Assert.Equal(["app-0.2.0", "app-0.3.0", "not-an-app"], AppFolders(Root));
        Assert.True(File.Exists(Path.Combine(Root, "app-file.txt")));
        Assert.Empty(_starter.Started);
        Assert.DoesNotContain("release", _events);
    }

    [Fact]
    public async Task Takeover_Healthy_JunctionsAreUnlinkedNotFollowed()
    {
        var outside = Path.Combine(_temp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Junction.Create(Path.Combine(Root, "app-0.0.9"), outside);            // an old "version" that is a junction
        Junction.Create(Path.Combine(Root, "app-0.1.0", "linked"), outside);  // a junction inside an old version

        await Takeover.RunAsync(Previous, () => Task.FromResult(true), Env());

        Assert.False(Directory.Exists(Path.Combine(Root, "app-0.0.9")));
        Assert.False(Directory.Exists(Path.Combine(Root, "app-0.1.0")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task Takeover_NewVersionFailsBeforeHealthy_RestartsPrevious()
    {
        var code = await Takeover.RunAsync(Previous, () => Task.FromResult(false), Env());

        Assert.NotEqual(0, code);
        Assert.Equal(Takeover.RolledBack, code);
        var (exe, args, cwd) = Assert.Single(_starter.Started);
        Assert.Equal(Path.Combine(Previous, "ProcessTracker.Service.exe"), exe);
        Assert.Equal(["--user"], args);
        Assert.Equal(Previous, cwd);
        Assert.Empty(_schtasks); // Register was not called
        Assert.Equal(["release", "start"], _events); // our mutex is gone before the previous version starts
        Assert.Equal(["app-0.1.0", "app-0.2.0", "app-0.3.0"], AppFolders(Root)); // nothing pruned
        Assert.True(File.Exists(Path.Combine(New, Takeover.FailedMarkerName))); // not retried by the worker
    }

    [Fact]
    public async Task Takeover_HealthCheckThrows_RestartsPrevious()
    {
        var code = await Takeover.RunAsync(Previous, () => throw new InvalidOperationException("host failed"), Env());

        Assert.Equal(Takeover.RolledBack, code);
        Assert.Single(_starter.Started);
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task Takeover_PreviousCannotStart_StillExitsNonZero()
    {
        _starter.Throw = new System.ComponentModel.Win32Exception(2);

        Assert.Equal(Takeover.RolledBack, await Takeover.RunAsync(Previous, () => Task.FromResult(false), Env()));
    }

    [Fact]
    public async Task Takeover_ExitRequestedBeforeHealthy_DoesNotRollBack()
    {
        // The MSI (or a newer version) asked this instance to exit; starting the old one would fight it.
        _exitRequested = true;

        var code = await Takeover.RunAsync(Previous, () => Task.FromResult(false), Env());

        Assert.Equal(0, code);
        Assert.Empty(_starter.Started);
        Assert.Empty(_schtasks);
    }

    [Theory]
    [InlineData(@"bin\Debug")]                              // a manual run from a build folder
    [InlineData(@"Other\Programs\ProcessTracker\app-0.3.0")] // looks like the layout, but not under LocalAppData
    public async Task Takeover_OutsideInstallLayout_DoesNotRegister(string relativeNewDir)
    {
        var newDir = Path.Combine(_temp.Path, relativeNewDir);
        Directory.CreateDirectory(newDir);
        var oldSibling = Path.Combine(Path.GetDirectoryName(newDir)!, "app-old");
        Directory.CreateDirectory(oldSibling);

        Assert.Equal(0, await Takeover.RunAsync(Previous, () => Task.FromResult(true), Env(newDir)));

        Assert.Empty(_schtasks); // the logon task keeps pointing at the installed version
        Assert.True(Directory.Exists(oldSibling));
        Assert.Equal(["app-0.1.0", "app-0.2.0", "app-0.3.0"], AppFolders(Root));
    }

    // FirstTickAsync: the "healthy" signal the program passes as becameHealthy.

    private static readonly TrackerView Ticked =
        new(new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc), default, FrozenDictionary<ProcessKey, ProcessView>.Empty, []);

    [Fact]
    public async Task FirstTick_TrueOnceTheCollectorHasTicked()
    {
        var host = new FakeTrackerHost();
        var wait = Takeover.FirstTickAsync(() => host, new TaskCompletionSource().Task, TimeSpan.FromSeconds(30), TimeProvider.System);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        host.View = Ticked;

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstTick_FalseWhenNoTickWithinTheLimit()
    {
        var host = new FakeTrackerHost();

        Assert.False(await Takeover.FirstTickAsync(() => host, new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(200), TimeProvider.System)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstTick_FalseAtOnceWhenTheHostEnds()
    {
        var ended = new TaskCompletionSource();
        var wait = Takeover.FirstTickAsync(() => null, ended.Task, TimeSpan.FromMinutes(5), TimeProvider.System);

        ended.SetResult();

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
