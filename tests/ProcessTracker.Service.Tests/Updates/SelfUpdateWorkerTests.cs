using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Core.Updates;
using ProcessTracker.Service.Tests.Fakes;
using ProcessTracker.Service.Updates;

namespace ProcessTracker.Service.Tests.Updates;

/// <summary>All with fakes: a fake GitHub handler, a recording starter and temporary folders.</summary>
public sealed class SelfUpdateWorkerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeStarter _starter = new();
    private bool _machineService;
    private int _probes;

    private string InstallRoot => Path.Combine(_temp.Path, "Programs", "ProcessTracker");
    private string CurrentAppDir => Path.Combine(InstallRoot, "app-0.2.0");
    private string NewAppDir => Path.Combine(InstallRoot, "app-0.3.0");

    public SelfUpdateWorkerTests() => Directory.CreateDirectory(CurrentAppDir);

    public void Dispose() => _temp.Dispose();

    private SelfUpdateWorker Worker(TimeSpan? firstCheck = null, TimeSpan? interval = null, TimeSpan? downloadTimeout = null,
        TimeProvider? time = null) => new(
        new ReleaseClient(_github, "0.2.0"),
        new SelfUpdateEnvironment
        {
            CurrentAppDir = CurrentAppDir,
            CurrentVersion = new Version(0, 2, 0),
            InstallRoot = InstallRoot,
            TempDir = Path.Combine(_temp.Path, "tmp"),
            MachineServiceInstalled = () => { Interlocked.Increment(ref _probes); return _machineService; },
            Starter = _starter,
            FirstCheck = firstCheck ?? TimeSpan.FromMinutes(10),
            Interval = interval ?? TimeSpan.FromHours(24),
            DownloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15),
        },
        time ?? TimeProvider.System,
        NullLogger<SelfUpdateWorker>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Worker_MachineServiceInstalled_DoesNothing()
    {
        _machineService = true;

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.MachineService, result);
        Assert.Empty(_github.Requests);
        Assert.Empty(_starter.Started);
        Assert.False(Directory.Exists(NewAppDir));
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("v0.1.9")]
    public async Task Worker_NotNewer_DoesNothing(string tag)
    {
        _github.Tag = tag;

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.UpToDate, result);
        Assert.Single(_github.Requests); // only the latest-release query, no download
        Assert.Empty(_starter.Started);
        Assert.Equal(["app-0.2.0"], Directory.GetDirectories(InstallRoot).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Worker_NoRelease_DoesNothing()
    {
        _github.ApiFails = true;

        Assert.Equal(UpdateCheck.NoRelease, await Worker().CheckOnceAsync(Ct));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Worker_Newer_ExtractsAndStartsTakeover()
    {
        // A leftover folder of the same version is replaced, not merged.
        Directory.CreateDirectory(NewAppDir);
        File.WriteAllText(Path.Combine(NewAppDir, "stale.txt"), "old");

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.Started, result);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(NewAppDir, "ProcessTracker.Service.exe")));
        Assert.Equal("cli", File.ReadAllText(Path.Combine(NewAppDir, "bin", "proctrack.exe")));
        Assert.False(File.Exists(Path.Combine(NewAppDir, "stale.txt")));

        var (exe, args, cwd) = Assert.Single(_starter.Started);
        Assert.Equal(Path.Combine(NewAppDir, "ProcessTracker.Service.exe"), exe);
        Assert.Equal(["--user", "--takeover", CurrentAppDir], args);
        Assert.Equal(NewAppDir, cwd);

        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "tmp", "ProcessTracker-update-0.3.0"))); // download cleaned up
    }

    [Fact]
    public async Task Worker_DownloadFails_NoExtractNoStart()
    {
        _github.SumsOverride = new string('0', 64); // hash mismatch

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.DownloadFailed, result);
        Assert.False(Directory.Exists(NewAppDir));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Worker_StalledDownload_GivesUpAfterTimeout()
    {
        _github.StallZip = true;

        var check = Worker(downloadTimeout: TimeSpan.FromMilliseconds(300)).CheckOnceAsync(Ct);
        var finished = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(30), Ct));

        Assert.Same(check, finished);
        Assert.Equal(UpdateCheck.DownloadFailed, await check);
        Assert.False(Directory.Exists(NewAppDir));
        Assert.Empty(_starter.Started);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "tmp", "ProcessTracker-update-0.3.0")));
    }

    [Fact]
    public void Environment_DownloadTimeout_Defaults15Minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(15), new SelfUpdateEnvironment
        {
            CurrentAppDir = @"C:\a", CurrentVersion = new Version(1, 0, 0), InstallRoot = @"C:\b", TempDir = @"C:\c",
            MachineServiceInstalled = () => false, Starter = new FakeStarter(),
        }.DownloadTimeout);

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("sub/../../escaped.txt")]
    public async Task Worker_ZipEntryEscapingTarget_NothingExtractedNoStart(string entry)
    {
        _github.Zip = FakeGitHub.BuildZip(("ProcessTracker.Service.exe", "new exe"), (entry, "evil"));

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.ExtractFailed, result);
        Assert.False(File.Exists(Path.Combine(InstallRoot, "escaped.txt")));
        Assert.False(Directory.Exists(NewAppDir));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Worker_ZipWithoutServiceExe_NoStart()
    {
        _github.Zip = FakeGitHub.BuildZip(("readme.txt", "nothing to run"));

        Assert.Equal(UpdateCheck.ExtractFailed, await Worker().CheckOnceAsync(Ct));
        Assert.False(Directory.Exists(NewAppDir));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Worker_VersionThatFailedTakeoverBefore_IsNotRetried()
    {
        Directory.CreateDirectory(NewAppDir);
        File.WriteAllText(Path.Combine(NewAppDir, Takeover.FailedMarkerName), "no tick");

        var result = await Worker().CheckOnceAsync(Ct);

        Assert.Equal(UpdateCheck.PreviouslyFailed, result);
        Assert.Single(_github.Requests);
        Assert.Empty(_starter.Started);
        Assert.True(File.Exists(Path.Combine(NewAppDir, Takeover.FailedMarkerName)));
    }

    [Fact]
    public async Task Worker_StarterThrows_ReportsStartFailed()
    {
        _starter.Throw = new System.ComponentModel.Win32Exception(2);

        Assert.Equal(UpdateCheck.StartFailed, await Worker().CheckOnceAsync(Ct));
    }

    /// <summary>Waits (real time, bounded) for something the worker does on its own thread after a timer fired.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, Ct);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task Worker_FirstCheckWaitsForTheFirstDelay()
    {
        _machineService = true; // each check only probes
        var time = new SteppedTimeProvider();
        using var worker = Worker(firstCheck: TimeSpan.FromMinutes(10), time: time);

        await worker.StartAsync(Ct);
        await Until(() => time.ArmedTimers == 1, "the worker waits on the first delay");
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref _probes)); // no timer fired, so nothing can have run
        time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => Volatile.Read(ref _probes) == 1, "the first check runs once the first delay has passed");
        await worker.StopAsync(Ct);
    }

    [Fact]
    public async Task Worker_ChecksOnceThenWaitsForTheInterval()
    {
        _machineService = true;
        var time = new SteppedTimeProvider();
        using var worker = Worker(firstCheck: TimeSpan.FromMinutes(10), interval: TimeSpan.FromHours(24), time: time);

        await worker.StartAsync(Ct);
        await Until(() => time.ArmedTimers == 1, "the worker waits on the first delay");
        time.Advance(TimeSpan.FromMinutes(10));
        await Until(() => Volatile.Read(ref _probes) == 1 && time.ArmedTimers == 1, "one check, then a wait on the interval");
        time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref _probes));
        time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => Volatile.Read(ref _probes) == 2, "the second check runs after the interval");
        await worker.StopAsync(Ct);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\ProcessTracker\app-0.2.0", @"C:\Users\x\AppData\Local\Programs\ProcessTracker")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\ProcessTracker\app-0.2.0\", @"C:\Users\x\AppData\Local\Programs\ProcessTracker")]
    [InlineData(@"C:\Users\x\AppData\Local\programs\processtracker\APP-0.2.0", @"C:\Users\x\AppData\Local\programs\processtracker")]
    [InlineData(@"C:\repos\ProcessTracker\src\ProcessTracker.Service\bin\Debug\net10.0-windows\win-x64", null)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\ProcessTracker", null)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Other\app-0.2.0", null)]
    [InlineData(@"C:\Users\y\AppData\Local\Programs\ProcessTracker\app-0.2.0", null)]
    public void InstallRootFor_OnlyTheUserModeLayout(string appDir, string? expected) =>
        Assert.Equal(expected, SelfUpdateWorker.InstallRootFor(appDir, @"C:\Users\x\AppData\Local"));

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0-dev.202611231200", "0.2.0")]
    [InlineData("1.10.3+abc", "1.10.3")]
    [InlineData("garbage", null)]
    [InlineData("1.2", null)]
    public void NumericVersion_TakesXyz(string informational, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), SelfUpdateWorker.NumericVersion(informational));
}
