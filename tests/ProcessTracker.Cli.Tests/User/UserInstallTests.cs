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
    /// <summary>The app folder of every /Create, in order (read from the task XML the fake is handed).</summary>
    private readonly List<string> _registered = [];
    private readonly List<(string Exe, string CommandLine, IReadOnlyDictionary<string, string> Env)> _cleanups = [];
    private readonly StringWriter _out = new(), _err = new();
    private string TaskXml = "";
    private int _signals;
    private int _stops;
    private bool _stopSucceeds = true;
    /// <summary>When set, the result of the n-th stop (1-based); otherwise <see cref="_stopSucceeds"/>.</summary>
    private Func<int, bool>? _stopResult;

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

    private static HealthDto Health(string mode, string owner = Me, string version = "1.0.0") =>
        new(version, DateTime.UtcNow, null, null, mode, mode == "user" ? owner : null);

    private UserInstall Make(bool elevated = false, bool machineService = false, Func<HealthDto?>? health = null,
        string? localAppData = null, bool runningFromInside = false, TimeSpan? startRetry = null)
    {
        var task = new UserTask((exe, args, _) =>
        {
            _schtasks.Add(string.Join(' ', args.Select(a => a.EndsWith(".xml") ? "<file>" : a)));
            if (args[0] == "/Create")
            {
                var xml = File.ReadAllText(args.First(a => a.EndsWith(".xml")));
                var command = System.Text.RegularExpressions.Regex.Match(xml, "<Command>([^<]+)</Command>").Groups[1].Value;
                _registered.Add(Path.GetDirectoryName(command)!);
            }
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
            StartCleanup = (exe, line, env) => _cleanups.Add((exe, line, env)),
            ProcessId = 4242,
            Health = _ => Task.FromResult(health?.Invoke()),
            SignalExit = () => { _signals++; return true; },
            StopInstance = _ => { _stops++; return _stopResult?.Invoke(_stops) ?? _stopSucceeds; },
            AppVersion = FolderVersion,
            LocalAppData = localAppData ?? _root,
            HealthTimeout = TimeSpan.FromMilliseconds(100),
            StopTimeout = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(10),
            StartRetryInterval = startRetry ?? TimeSpan.FromSeconds(5),
            Out = _out,
            Err = _err,
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The version an app folder holds, from its app-X.Y.Z name (the fake exe has no version resource).</summary>
    private static Version? FolderVersion(string dir) =>
        Path.GetFileName(dir) is { } name && name.StartsWith("app-", StringComparison.Ordinal) && Version.TryParse(name[4..], out var v) ? v : null;

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
        var changes = _schtasks.Where(c => !c.StartsWith("/Query")).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal($@"/Create /TN \ProcessTracker\User-{Sid} /XML <file> /F", changes[0]);
        Assert.Equal($@"/Run /TN \ProcessTracker\User-{Sid}", changes[1]);
    }

    [Fact]
    public async Task Install_SameVersionAlreadyRunning_DoesNotRestart()
    {
        var code = await Make(health: () => Health("user", owner: @"pc\ME", version: "1.0.0")).InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.Equal(0, _stops);
        Assert.DoesNotContain(_schtasks, c => c.StartsWith("/Run"));
    }

    [Fact]
    public async Task Install_OlderVersionRunning_RestartsAndWaitsForNewVersion()
    {
        // 0.9.0 answers until it is stopped; then nothing for a few polls; then the task's new 1.0.0.
        var stoppedAtPoll = -1;
        var polls = 0;
        HealthDto? Answer()
        {
            polls++;
            if (_stops == 0) return Health("user", version: "0.9.0");
            if (stoppedAtPoll < 0) stoppedAtPoll = polls;
            return polls - stoppedAtPoll < 3 ? null : Health("user", version: "1.0.0-dev.202611231200");
        }

        var code = await Make(health: Answer).InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.Equal(1, _stops);
        Assert.Contains(_schtasks, c => c.StartsWith("/Run"));
        Assert.True(polls - stoppedAtPoll >= 3, "returned before the new version answered");
        Assert.Contains("0.9.0", _out.ToString());
        Assert.Contains("1.0.0", _out.ToString());
    }

    [Fact]
    public async Task Install_SameVersionFromAnotherFolderRunning_Restarts()
    {
        TaskXml = UserTask.BuildXml(Path.Combine(Programs, "elsewhere", "app-1.0.0"), Sid);
        var code = await Make(health: () => Health("user", version: "1.0.0")).InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.Equal(1, _stops);
        Assert.Contains(_schtasks, c => c.StartsWith("/Run"));
    }

    [Fact]
    public async Task Install_NothingAnswersAfterStart_StartsTaskAgain()
    {
        var polls = 0;
        var code = await Make(health: () => ++polls < 8 ? null : Health("user"), startRetry: TimeSpan.FromMilliseconds(25))
            .InstallAsync(AppDir(), Ct);

        Assert.Equal(0, code);
        Assert.True(_schtasks.Count(c => c.StartsWith("/Run")) >= 2, string.Join(" | ", _schtasks));
    }

    [Fact]
    public async Task Install_NewVersionNeverHealthy_RollsBackToRunningPrevious()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        // The new 1.0.0 never answers; only 0.9.0 does (before the stop, and again after the rollback starts it).
        var code = await Make(health: () => Health("user", version: "0.9.0")).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered); // registration restored
        Assert.Equal(2, _stops); // the old copy, then whatever the failed start left behind
        var changes = _schtasks.Where(c => !c.StartsWith("/Query")).ToList();
        Assert.StartsWith("/Run", changes[^1]); // the previous copy is started again, after the task points at it
        Assert.Contains("1.0.0 did not start", _err.ToString());
        Assert.Contains("previous version 0.9.0 is running again", _err.ToString());
        Assert.DoesNotContain("next logon", _err.ToString());
    }

    [Fact]
    public async Task Install_OldVersionDoesNotStop_RollsTaskBackWithoutClaimingItRunsAgain()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        _stopSucceeds = false;
        // One StartedUtc throughout: the same old copy, asked to exit and still answering, never a restarted one.
        var old = Health("user", version: "0.9.0");
        var code = await Make(health: () => old).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal(1, _stops);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered); // registration restored
        var changes = _schtasks.Where(c => !c.StartsWith("/Query")).ToList();
        Assert.StartsWith("/Create", changes[1]);
        Assert.All(changes.Skip(2), c => Assert.StartsWith("/Run", c)); // the previous copy is asked to start again
        Assert.Contains("did not stop", _err.ToString());
        Assert.DoesNotContain("running again", _err.ToString());
    }

    [Fact]
    public async Task Install_OldVersionStopsLate_RollbackStartsPreviousAgain()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        _stopSucceeds = false;
        // The old copy answers for a while after the (timed-out) stop, then exits; a /Run issued after that starts the
        // previous version again (one issued while the old copy still ran is ignored, as MultipleInstancesPolicy IgnoreNew does).
        var old = Health("user", version: "0.9.0");
        var polls = 0;
        var runsWhenGone = -1;
        HealthDto? restarted = null;
        HealthDto? Answer()
        {
            polls++;
            if (_stops == 0 || polls < 6) return old;
            var runs = _schtasks.Count(c => c.StartsWith("/Run"));
            if (runsWhenGone < 0) runsWhenGone = runs;
            if (runs > runsWhenGone) restarted ??= Health("user", version: "0.9.0");
            return restarted;
        }

        var code = await Make(health: Answer, startRetry: TimeSpan.FromMilliseconds(20)).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered);
        Assert.True(_schtasks.Count(c => c.StartsWith("/Run")) >= 2, string.Join(" | ", _schtasks));
        Assert.Contains("previous version 0.9.0 is running again", _err.ToString());
    }

    [Fact]
    public async Task Install_NewVersionDoesNotStop_RestoresTaskWithoutStartingPrevious()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        _stopResult = n => n == 1; // the old copy stops; the failed new one hangs and keeps the instance mutex
        var old = Health("user", version: "0.9.0");
        var code = await Make(health: () => _stops == 0 ? old : null).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal(2, _stops);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered);
        var changes = _schtasks.Where(c => !c.StartsWith("/Query")).ToList();
        Assert.StartsWith("/Create", changes[^1]); // the hung copy holds the mutex: starting the previous one is pointless
        Assert.Contains("did not stop", _err.ToString());
        Assert.DoesNotContain("running again", _err.ToString());
        Assert.Contains("points at the previous version 0.9.0", _err.ToString());
    }

    [Fact]
    public async Task Install_RunningCopyWithoutTask_NewNeverHealthy_SaysTheStoppedCopyIsGone()
    {
        // A copy of this user's runs (started by hand, say) but no task is registered: the rollback has nothing to start it with.
        var old = Health("user", version: "0.9.0");
        var code = await Make(health: () => _stops == 0 ? old : null).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.StartsWith("/Delete", _schtasks.Where(c => !c.StartsWith("/Query")).Last());
        Assert.Contains("ProcessTracker 0.9.0 that was running was stopped and is not running now", _err.ToString());
    }

    [Fact]
    public async Task Install_SameFolderAgain_NeverHealthy_DoesNotCallItThePreviousVersion()
    {
        var dir = AppDir();
        TaskXml = UserTask.BuildXml(dir, Sid);
        var code = await Make(health: () => null).InstallAsync(dir, Ct);

        Assert.Equal(1, code);
        Assert.Contains($"The task still points at {dir}.", _err.ToString());
        Assert.DoesNotContain("previous version", _err.ToString());
    }

    [Fact]
    public async Task Install_OtherUserAnswersAfterStart_RollsBack()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        var polls = 0;
        var code = await Make(health: () => ++polls == 1 ? null : Health("user", owner: @"PC\someoneelse")).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered); // registration restored
        Assert.Contains("belongs to another user", _err.ToString());
    }

    [Fact]
    public async Task Install_FirstInstall_OtherUserAnswersAfterStart_RemovesTheTask()
    {
        var polls = 0;
        var code = await Make(health: () => ++polls == 1 ? null : Health("user", owner: @"PC\someoneelse")).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.StartsWith("/Delete", _schtasks.Where(c => !c.StartsWith("/Query")).Last());
        Assert.Contains("belongs to another user", _err.ToString());
    }

    [Fact]
    public async Task Install_StoppedPreviousInstall_NewNeverHealthy_RestoresTaskWithoutStartingIt()
    {
        var previous = Path.Combine(Programs, "app-0.9.0");
        TaskXml = UserTask.BuildXml(previous, Sid);
        var code = await Make(health: () => null).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0"), previous], _registered);
        var changes = _schtasks.Where(c => !c.StartsWith("/Query")).ToList();
        Assert.StartsWith("/Create", changes[^1]); // nothing was running before, so nothing is started after the rollback
        Assert.Contains("points at the previous version", _err.ToString());
    }

    [Fact]
    public async Task Install_FirstInstall_NeverHealthy_RemovesTheTask()
    {
        var code = await Make(health: () => null).InstallAsync(AppDir(), Ct);

        Assert.Equal(1, code);
        Assert.Equal([Path.Combine(Programs, "app-1.0.0")], _registered);
        Assert.StartsWith("/Delete", _schtasks.Where(c => !c.StartsWith("/Query")).Last());
        Assert.Contains("task was removed", _err.ToString());
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
        Assert.Equal(Programs, cleanup.Env[UserInstall.CleanupDirVariable]);
        Assert.Contains("Wait-Process -Id 4242", UserInstall.CleanupScript(4242));
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

    [Fact]
    public void CleanupCommand_KeepsFolderOutOfScriptText()
    {
        var script = UserInstall.CleanupScript(7);
        var line = UserInstall.CleanupCommandLine(7);

        Assert.DoesNotContain("'", script);
        Assert.Contains("$env:" + UserInstall.CleanupDirVariable, script);
        var encoded = line[(line.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length)..];
        Assert.Equal(script, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public async Task Uninstall_PathWithQuotesAndSpace_TravelsOnlyInEnvironment()
    {
        var local = Path.Combine(_root, "O'Brien \u2019x\u2018 dir");
        var programs = Path.Combine(local, "Programs", "ProcessTracker");
        Directory.CreateDirectory(programs);

        var code = await Make(localAppData: local, runningFromInside: true).UninstallAsync(purgeData: false, Ct);

        Assert.Equal(0, code);
        var cleanup = Assert.Single(_cleanups);
        Assert.Equal(programs, cleanup.Env[UserInstall.CleanupDirVariable]);
        Assert.DoesNotContain("Brien", cleanup.CommandLine);
        var encoded = cleanup.CommandLine[(cleanup.CommandLine.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)..];
        Assert.DoesNotContain("Brien", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }
}
