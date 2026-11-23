using ProcessTracker.Cli.User;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Tests.User;

/// <summary>All collaborators are fakes: no schtasks, no process start, no real folders outside a temp root.</summary>
public sealed class UserInstallTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proctrack-userinstall-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _schtasks = [];
    private readonly List<string> _started = [];
    private readonly StringWriter _out = new(), _err = new();
    private string TaskXml = "";
    private int _signals;

    public UserInstallTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AppDir()
    {
        var dir = Path.Combine(_root, "Programs", "app-1.0.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, UserTask.ServiceExeName), "");
        return dir;
    }

    private static HealthDto Health(string mode) => new("1.0.0", DateTime.UtcNow, null, null, mode, mode == "user" ? @"PC\me" : null);

    private UserInstall Make(bool elevated = false, bool machineService = false, Func<HealthDto?>? health = null,
        string? programsDir = null, string? dataDir = null)
    {
        var task = new UserTask((exe, args, _) =>
        {
            _schtasks.Add(string.Join(' ', args));
            if (args[0] == "/Query") return TaskXml.Length > 0 ? (0, TaskXml) : (1, "not found");
            return (0, "");
        }, Sid);
        return new UserInstall(new UserInstallEnvironment
        {
            Task = task,
            IsElevated = () => elevated,
            MachineServiceInstalled = () => machineService,
            StartDetached = (exe, dir) => _started.Add(exe),
            Health = _ => Task.FromResult(health?.Invoke()),
            SignalExit = () => { _signals++; return true; },
            ProgramsDir = programsDir ?? Path.Combine(_root, "Programs"),
            DataDir = dataDir ?? Path.Combine(_root, "Data"),
            HealthTimeout = TimeSpan.FromMilliseconds(100),
            StopTimeout = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(10),
            Out = _out,
            Err = _err,
        });
    }

    [Fact]
    public async Task UserInstall_Elevated_Refuses()
    {
        var code = await Make(elevated: true).InstallAsync(AppDir(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("run this from a normal, non-administrator terminal", _err.ToString());
        Assert.Empty(_schtasks);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task UserInstall_MachineServicePresent_Refuses()
    {
        var code = await Make(machineService: true).InstallAsync(AppDir(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("machine-wide", _err.ToString());
        Assert.Empty(_schtasks);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task UserInstall_MissingExe_Refuses()
    {
        var code = await Make().InstallAsync(Path.Combine(_root, "nowhere"), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Empty(_schtasks);
    }

    [Fact]
    public async Task UserInstall_RegistersStartsAndWaitsForUserMode()
    {
        var polls = 0;
        var code = await Make(health: () => ++polls < 4 ? null : Health("user")).InstallAsync(AppDir(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains(_schtasks, c => c.StartsWith("/Create"));
        Assert.Single(_started);
        Assert.EndsWith(UserTask.ServiceExeName, _started[0]);
    }

    [Fact]
    public async Task UserInstall_NeverReportsUserMode_FailsAfterTimeout()
    {
        var code = await Make(health: () => Health("service")).InstallAsync(AppDir(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("mode=user", _err.ToString());
    }

    [Fact]
    public async Task Uninstall_KeepsDataWithoutPurge()
    {
        var programs = Path.Combine(_root, "Programs");
        var data = Path.Combine(_root, "Data");
        AppDir();
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.db"), "x");
        TaskXml = UserTask.BuildXml(Path.Combine(programs, "app-1.0.0"), Sid);

        var code = await Make(programsDir: programs, dataDir: data).UninstallAsync(purgeData: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal(1, _signals);
        Assert.Contains(_schtasks, c => c.StartsWith("/Delete"));
        Assert.False(Directory.Exists(programs));
        Assert.True(File.Exists(Path.Combine(data, "state.db")));
    }

    [Fact]
    public async Task Uninstall_PurgeDeletesData()
    {
        var data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(data);

        var code = await Make(dataDir: data).UninstallAsync(purgeData: true, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(data));
        Assert.DoesNotContain(_schtasks, c => c.StartsWith("/Delete")); // no task was registered
    }

    [Fact]
    public async Task Status_ReportsTaskHealthAndMachineService()
    {
        TaskXml = UserTask.BuildXml(@"C:\apps\app-1.0.0", Sid);

        var s = await Make(machineService: true, health: () => Health("user")).StatusAsync(TestContext.Current.CancellationToken);

        Assert.True(s.TaskRegistered);
        Assert.Equal(@"C:\apps\app-1.0.0", s.AppDir);
        Assert.True(s.Responding);
        Assert.Equal("user", s.Mode);
        Assert.Equal(@"PC\me", s.Owner);
        Assert.True(s.MachineServiceInstalled);
    }

    [Fact]
    public async Task Status_NothingInstalled()
    {
        var s = await Make().StatusAsync(TestContext.Current.CancellationToken);

        Assert.False(s.TaskRegistered);
        Assert.False(s.Responding);
        Assert.Null(s.Mode);
    }
}
