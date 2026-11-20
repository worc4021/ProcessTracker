using ProcessTracker.Updater;

namespace ProcessTracker.Updater.Tests;

public sealed class UpgradeOrchestratorTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-updater-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeJunction _junction = new();
    private readonly FakeService _service;
    private readonly FakeHealth _health;
    private readonly List<string> _log = [];

    public UpgradeOrchestratorTests()
    {
        _service = new FakeService(_junction, Link);
        _health = new FakeHealth(_service);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Link => Path.Combine(_root, "current");

    private string VersionDir(string v) => Path.Combine(_root, "versions", v);

    /// <summary>Creates version folders, the i-th one created i hours after T0, and points current at <paramref name="current"/>.</summary>
    private void Layout(string current, params string[] versions)
    {
        for (var i = 0; i < versions.Length; i++)
        {
            var dir = Directory.CreateDirectory(VersionDir(versions[i]));
            File.WriteAllText(Path.Combine(dir.FullName, "ProcessTracker.Service.exe"), versions[i]);
            Directory.SetCreationTimeUtc(dir.FullName, T0.AddHours(i));
        }
        _junction.Point(Link, VersionDir(current));
        _service.Start(); // the old version is serving before the upgrade
        _service.Calls.Clear();
    }

    private UpgradeOrchestrator Orchestrator() =>
        new(_service, _junction, _health, _log.Add, pollInterval: TimeSpan.FromMilliseconds(10));

    private string[] RemainingVersions() =>
        Directory.GetDirectories(Path.Combine(_root, "versions")).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public async Task HealthyNewVersion_Upgraded_PrunesToThree()
    {
        Layout("1.0.4", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5");

        var outcome = await Orchestrator().RunAsync(_root, "1.0.5", Timeout);

        Assert.Equal(UpgradeOutcome.Upgraded, outcome);
        Assert.Equal(VersionDir("1.0.5"), _junction.Target(Link));
        Assert.Equal(["stop", "start"], _service.Calls);
        Assert.Equal(["1.0.3", "1.0.4", "1.0.5"], RemainingVersions());
    }

    [Fact]
    public async Task NewVersionNeverHealthy_RolledBackToPrevious()
    {
        Layout("1.0.1", "1.0.1", "1.0.2", "1.0.3", "1.0.4");
        _health.Reported = v => v == "1.0.4" ? new HealthResult("unhealthy", 0.5) : new HealthResult(v, 0.5);

        var outcome = await Orchestrator().RunAsync(_root, "1.0.4", Timeout);

        Assert.Equal(UpgradeOutcome.RolledBack, outcome);
        Assert.Equal(VersionDir("1.0.1"), _junction.Target(Link));
        Assert.Equal("1.0.1", _service.RunningVersion);
        Assert.Equal(["stop", "start", "stop", "start"], _service.Calls);
        Assert.Equal(["1.0.1", "1.0.2", "1.0.3", "1.0.4"], RemainingVersions()); // no prune after a rollback
    }

    [Fact]
    public async Task StartThrows_RolledBack()
    {
        Layout("1.0.1", "1.0.1", "1.0.2");
        _service.ThrowOnStartOf = "1.0.2";

        var outcome = await Orchestrator().RunAsync(_root, "1.0.2", Timeout);

        Assert.Equal(UpgradeOutcome.RolledBack, outcome);
        Assert.Equal(VersionDir("1.0.1"), _junction.Target(Link));
        Assert.Equal("1.0.1", _service.RunningVersion);
    }

    [Fact]
    public async Task StopThrows_RolledBack()
    {
        Layout("1.0.1", "1.0.1", "1.0.2");
        _service.StopFailures = 1;

        var outcome = await Orchestrator().RunAsync(_root, "1.0.2", Timeout);

        Assert.Equal(UpgradeOutcome.RolledBack, outcome);
        Assert.Equal(VersionDir("1.0.1"), _junction.Target(Link));
        Assert.Equal("1.0.1", _service.RunningVersion);
    }

    [Fact]
    public async Task PointThrows_RolledBack()
    {
        Layout("1.0.1", "1.0.1", "1.0.2");
        _junction.ThrowOnPointTo = VersionDir("1.0.2");

        var outcome = await Orchestrator().RunAsync(_root, "1.0.2", Timeout);

        Assert.Equal(UpgradeOutcome.RolledBack, outcome);
        Assert.Equal(VersionDir("1.0.1"), _junction.Target(Link));
        Assert.Equal("1.0.1", _service.RunningVersion);
        Assert.Equal(["stop", "stop", "start"], _service.Calls);
    }

    [Fact]
    public async Task NoPreviousVersion_FailsBeforeStopping_NothingChanged()
    {
        Directory.CreateDirectory(VersionDir("1.0.2")); // no current junction

        await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().RunAsync(_root, "1.0.2", Timeout));

        Assert.Empty(_service.Calls);
        Assert.Null(_junction.Target(Link));
    }

    [Fact]
    public async Task PruneFailure_DoesNotRollBackAHealthyUpgrade()
    {
        Layout("1.0.4", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5");
        var orchestrator = new UpgradeOrchestrator(_service, _junction, _health,
            m => { if (m.StartsWith("Pruning ", StringComparison.Ordinal)) throw new InvalidOperationException("boom"); _log.Add(m); },
            pollInterval: TimeSpan.FromMilliseconds(10));

        var outcome = await orchestrator.RunAsync(_root, "1.0.5", Timeout);

        Assert.Equal(UpgradeOutcome.Upgraded, outcome);
        Assert.Equal(VersionDir("1.0.5"), _junction.Target(Link));
        Assert.Equal(["stop", "start"], _service.Calls);
        Assert.Contains(_log, l => l.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollbackAlsoUnhealthy_RollbackFailed()
    {
        Layout("1.0.1", "1.0.1", "1.0.2");
        _health.Reported = _ => null; // nothing answers

        var outcome = await Orchestrator().RunAsync(_root, "1.0.2", Timeout);

        Assert.Equal(UpgradeOutcome.RollbackFailed, outcome);
        Assert.Equal(VersionDir("1.0.1"), _junction.Target(Link)); // still pointed back
    }

    [Fact]
    public async Task Prune_NeverDeletesCurrentOrPrevious()
    {
        // The previous (1.0.1) and new (1.0.3) targets are among the oldest folders by creation time.
        Layout("1.0.1", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6");

        var outcome = await Orchestrator().RunAsync(_root, "1.0.3", Timeout);

        Assert.Equal(UpgradeOutcome.Upgraded, outcome);
        Assert.Equal(["1.0.1", "1.0.3", "1.0.4", "1.0.5", "1.0.6"], RemainingVersions());
    }

    [Fact]
    public async Task Prune_LockedFolder_LoggedAndOthersStillPruned()
    {
        Layout("1.0.4", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5");
        using (File.Open(Path.Combine(VersionDir("1.0.1"), "ProcessTracker.Service.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = await Orchestrator().RunAsync(_root, "1.0.5", Timeout);

            Assert.Equal(UpgradeOutcome.Upgraded, outcome);
        }
        Assert.DoesNotContain("1.0.2", RemainingVersions());
        Assert.Contains(_log, l => l.Contains("1.0.1", StringComparison.Ordinal) && l.Contains("prune", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeJunction : IJunction
    {
        private readonly Dictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);

        public string? ThrowOnPointTo { get; set; }

        public string? Target(string link) => _links.GetValueOrDefault(link);

        public void Point(string link, string target)
        {
            if (string.Equals(target, ThrowOnPointTo, StringComparison.OrdinalIgnoreCase))
                throw new IOException("mklink failed");
            _links[link] = target;
        }
    }

    /// <summary>A service that runs whatever version folder the junction points at when it starts.</summary>
    private sealed class FakeService(FakeJunction junction, string link) : IServiceControl
    {
        public List<string> Calls { get; } = [];
        public string? RunningVersion { get; private set; }
        public string? ThrowOnStartOf { get; set; }
        public int StopFailures { get; set; }

        public void Stop(TimeSpan timeout)
        {
            Calls.Add("stop");
            if (StopFailures > 0)
            {
                StopFailures--;
                throw new InvalidOperationException("service did not stop"); // still running the old version
            }
            RunningVersion = null;
        }

        public void Start()
        {
            Calls.Add("start");
            var version = Path.GetFileName(junction.Target(link));
            if (version == ThrowOnStartOf) throw new InvalidOperationException("service failed to start");
            RunningVersion = version;
        }
    }

    private sealed class FakeHealth(FakeService service) : IHealthProbe
    {
        /// <summary>What /api/health answers while the given version is running.</summary>
        public Func<string, HealthResult?> Reported { get; set; } = v => new HealthResult(v + "+abc123", 0.5);

        public Task<HealthResult?> GetAsync() =>
            Task.FromResult(service.RunningVersion is { } v ? Reported(v) : null);
    }
}
