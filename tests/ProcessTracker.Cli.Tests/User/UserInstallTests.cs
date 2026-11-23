using ProcessTracker.Cli.User;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Tests.User;

/// <summary>All collaborators are fakes: no schtasks, no process start, no real folders outside a temp root.</summary>
public sealed class UserInstallTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Me = @"PC\me";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proctrack-userinstall-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _schtasks = [];
    private readonly List<(string Exe, string CommandLine)> _cleanups = [];
    private readonly StringWriter _out = new(), _err = new();
    private string TaskXml = "";
    private int _signals;

    public UserInstallTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Programs => Path.Combine(_root, "Programs", "ProcessTracker");
    private string Data => Path.Combine(_root, "ProcessTracker");

    private string AppDir()
    {
        var dir = Path.Combine(Programs, "app-1.0.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, UserTask.ServiceExeName), "");
        return dir;
    }

    private static HealthDto Health(string mode, string owner = Me) => new("1.0.0", DateTime.UtcNow, null, null, mode, mode == "user" ? owner : null);

    private UserInstall Make(bool elevated = false, bool machineService = false, Func<HealthDto?>? health = null,
        string? localAppData = null, bool runningFromInside = false)
    {
        var task = new UserTask((exe, args, _) =>
        {
            _schtasks.Add(string.Join(' ', args.Select(a => a.EndsWith(".xml") ? "<file>" : a)));
            if (args[0] == "/Query") return TaskXml.Length > 0 ? (0, TaskXml) : (1, "not found");
            return (0, "");
        }, Sid);
        return new UserInstall(new UserInstallEnvironment
        {
            Task = task,
            IsElevated = () => elevated,
            MachineServiceInstalled = () => machineService,
            CurrentUser = () => Me,
            RunningFrom = _ => runningFromInside,
            StartCleanup = (exe, line) => _cleanups.Add((exe, line)),
            ProcessId = 4242,
            Health = _ => Task.FromResult(health?.Invoke()),
            SignalExit = () => { _signals++; return true; },
            LocalAppData = localAppData ?? _root,
            HealthTimeout = TimeSpan.FromMilliseconds(100),
            StopTimeout = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(10),
            Out = _out,
            Err = _err,
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UserInstall_Elevated_Refuses()
    {
        var code = await Make(elevated: true).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Contains("run this from a normal, non-administrator terminal", _err.ToString());
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task UserInstall_MachineServicePresent_Refuses()
    {
        var code = await Make(machineService: true).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Contains("machine-wide", _err.ToString());
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task UserInstall_MissingExe_Refuses()
    {
        var code = await Make().InstallAsync(Path.Combine(_root, "nowhere"), Ct);

        Assert.Equal(1, code);
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task Install_StartsViaTaskScheduler()
    {
        var polls = 0;
        var code = await Make(health: () => ++polls < 4 ? null : Health("user")).InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.Equal(2, _schtasks.Count);
        Assert.Equal($@"/Create /TN \ProcessTracker\User-{Sid} /XML <file> /F", _schtasks[0]);
        Assert.Equal($@"/Run /TN \ProcessTracker\User-{Sid}", _schtasks[1]);
    }

    [Fact]
    public async Task Install_OwnInstanceAlreadyRunning_DoesNotRunTask()
    {
        var code = await Make(health: () => Health("user", owner: @"pc\ME")).InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.DoesNotContain(_schtasks, c => c.StartsWith("/Run"));
    }

    [Fact]
    public async Task Install_OtherUsersInstanceAnswering_Refuses()
    {
        var code = await Make(health: () => Health("user", owner: @"PC\someoneelse")).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Contains("this ProcessTracker belongs to another user; ask an admin to install the machine-wide service", _err.ToString());
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task UserInstall_NeverReportsUserMode_FailsAfterTimeout()
    {
        var code = await Make(health: () => Health("service")).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Contains("mode=user", _err.ToString());
    }

    [Fact]
    public async Task Uninstall_KeepsDataWithoutPurge()
    {
        AppDir();
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "state.db"), "x");
        var sibling = Path.Combine(_root, "Programs", "Other");
        Directory.CreateDirectory(sibling);
        TaskXml = UserTask.BuildXml(Path.Combine(Programs, "app-1.0.0"), Sid);

        var code = await Make().UninstallAsync(purgeData: false, Ct);

        Assert.Equal(0, code);
        Assert.Equal(1, _signals);
        Assert.Contains(_schtasks, c => c.StartsWith("/Delete"));
        Assert.False(Directory.Exists(Programs));
        Assert.True(Directory.Exists(sibling));
        Assert.True(File.Exists(Path.Combine(Data, "state.db")));
        Assert.Empty(_cleanups);
    }

    [Fact]
    public async Task Uninstall_PurgeDeletesData()
    {
        Directory.CreateDirectory(Data);

        var code = await Make().UninstallAsync(purgeData: true, Ct);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(Data));
        Assert.DoesNotContain(_schtasks, c => c.StartsWith("/Delete")); // no task was registered
    }

    [Fact]
    public async Task Uninstall_RunningFromInsideFolder_SchedulesCleanup()
    {
        AppDir();

        var code = await Make(runningFromInside: true).UninstallAsync(purgeData: false, Ct);

        Assert.Equal(0, code);
        var cleanup = Assert.Single(_cleanups);
        Assert.Contains($"'{Programs}'", cleanup.CommandLine);
        Assert.Contains("Wait-Process -Id 4242", cleanup.CommandLine);
        Assert.Contains("finishes when this command exits", _out.ToString());
    }

    [Fact]
    public async Task Uninstall_RunningFromOutside_DeletesDirectly()
    {
        AppDir();

        await Make(runningFromInside: false).UninstallAsync(purgeData: false, Ct);

        Assert.False(Directory.Exists(Programs));
        Assert.Empty(_cleanups);
    }

    [Fact]
    public async Task Uninstall_EmptyLocalAppData_DeletesNothing()
    {
        AppDir();

        var code = await Make(localAppData: "").UninstallAsync(purgeData: true, Ct);

        Assert.Equal(1, code);
        Assert.Equal(0, _signals);
        Assert.Empty(_schtasks);
        Assert.True(Directory.Exists(Programs));
    }

    [Fact]
    public async Task Uninstall_RelativeLocalAppData_Refuses()
    {
        var code = await Make(localAppData: "relative").UninstallAsync(purgeData: true, Ct);

        Assert.Equal(1, code);
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task Status_ReportsTaskHealthAndMachineService()
    {
        TaskXml = UserTask.BuildXml(@"C:\apps\app-1.0.0", Sid);

        var s = await Make(machineService: true, health: () => Health("user")).StatusAsync(Ct);

        Assert.True(s.TaskRegistered);
        Assert.Equal(@"C:\apps\app-1.0.0", s.AppDir);
        Assert.True(s.Responding);
        Assert.Equal("user", s.Mode);
        Assert.Equal(Me, s.Owner);
        Assert.True(s.MachineServiceInstalled);
    }

    [Fact]
    public async Task Status_NothingInstalled()
    {
        var s = await Make().StatusAsync(Ct);

        Assert.False(s.TaskRegistered);
        Assert.False(s.Responding);
        Assert.Null(s.Mode);
    }
}
