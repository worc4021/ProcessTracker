using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class HostConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static ServiceOptions Resolve(params (string Key, string Value)[] settings) =>
        ServiceOptions.Resolve(Config(settings), Config(), RunMode.Dev);

    [Fact]
    public void Defaults_AreValid()
    {
        var o = Resolve();
        Assert.Empty(o.Validate());
        Assert.Equal(ServiceOptions.DevPort, o.Port);
    }

    [Theory]
    [InlineData("RetentionHours", "0")]
    [InlineData("RetentionHours", "-5")]
    [InlineData("RetentionHours", "NaN")]
    [InlineData("RetentionHours", "Infinity")]
    [InlineData("RetentionHours", "100000")]
    [InlineData("OrphanIdleAlertMinutes", "0")]
    [InlineData("OrphanIdleAlertMinutes", "NaN")]
    [InlineData("OrphanIdleAlertMinutes", "1e9")]
    public void OutOfRangeValues_AreRejected(string key, string value)
    {
        var errors = Resolve((key, value)).Validate();
        Assert.Contains(errors, e => e.StartsWith(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("")]
    public void InvalidHeavyCommandPattern_IsRejected(string pattern)
    {
        var errors = Resolve(("HeavyCommandPatterns:0", @"\bbazel\b"),("HeavyCommandPatterns:1", pattern)).Validate();
        Assert.Contains(errors, e => e.StartsWith("HeavyCommandPatterns[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void HeavyCommandPatterns_AreCompiledCaseInsensitive()
    {
        var o = Resolve(("HeavyCommandPatterns:0", @"\bbazel\s+build"));
        Assert.Empty(o.Validate());
        Assert.Matches(Assert.Single(o.HeavyCommandRegexes()), "BAZEL build //...");
    }

    [Fact]
    public void OrphanAlertImages_DefaultEmpty_AndBound()
    {
        Assert.Empty(Resolve().OrphanAlertImages);
        var o = Resolve(("OrphanAlertImages:0", "solver.exe"), ("OrphanAlertImages:1", "engine*.exe"));
        Assert.Empty(o.Validate());
        Assert.Equal(["solver.exe", "engine*.exe"], o.OrphanAlertImages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankOrphanAlertImage_IsRejected(string entry)
    {
        var errors = Resolve(("OrphanAlertImages:0", "solver.exe"), ("OrphanAlertImages:1", entry)).Validate();
        Assert.Contains(errors, e => e.StartsWith("OrphanAlertImages[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityThresholds_DefaultEmpty_ToActivityOptionsHasNoPerImage()
    {
        var o = Resolve();
        Assert.Empty(o.ActivityThresholds);
        Assert.Empty(o.ToActivityOptions().PerImage);
    }

    [Fact]
    public void ActivityThresholds_Bound_MissingFieldsTakeDefaults_CaseInsensitiveKeys()
    {
        var o = Resolve(("ActivityThresholds:solver.exe:CpuCores", "0.05"), ("ActivityThresholds:solver.exe:ContextSwitches", "2000"));
        Assert.Empty(o.Validate());

        var perImage = o.ToActivityOptions().PerImage;
        var t = perImage["SOLVER.EXE"];
        Assert.Equal(0.05f, t.CpuCores);
        Assert.Equal(2000, t.ContextSwitches);
        Assert.Equal(new ProcessTracker.Core.Activity.ActivityThresholds().IoOps, t.IoOps);
        Assert.Equal(new ProcessTracker.Core.Activity.ActivityThresholds().WriteOps, t.WriteOps);
    }

    [Theory]
    [InlineData("CpuCores", "-0.1")]
    [InlineData("CpuCores", "NaN")]
    [InlineData("IoOps", "-1")]
    [InlineData("WriteOps", "-1")]
    [InlineData("ContextSwitches", "-5")]
    public void NegativeActivityThreshold_IsRejected(string field, string value)
    {
        var errors = Resolve(($"ActivityThresholds:solver.exe:{field}", value)).Validate();
        Assert.Contains(errors, e => e.StartsWith("ActivityThresholds[solver.exe]", StringComparison.Ordinal));
    }

    [Fact]
    public void NewSettings_BindFromRealJson()
    {
        var json = """{"ActivityThresholds":{"Solver.exe":{"CpuCores":0.05}},"OrphanAlertImages":["a*"]}""";
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();

        var o = ServiceOptions.Resolve(config, Config(), RunMode.Dev);

        Assert.Empty(o.Validate());
        var defaults = new ProcessTracker.Core.Activity.ActivityThresholds();
        var t = o.ToActivityOptions().PerImage["SOLVER.EXE"];
        Assert.Equal(0.05f, t.CpuCores);
        Assert.Equal(defaults.IoOps, t.IoOps);
        Assert.Equal(defaults.WriteOps, t.WriteOps);
        Assert.Equal(defaults.ContextSwitches, t.ContextSwitches);
        Assert.Equal(["a*"], o.OrphanAlertImages);
    }

    [Fact]
    public void OutOfRangePort_OnCommandLine_IsRejected()
    {
        var errors = ServiceOptions.Resolve(Config(("Port", "70000")), Config(("Port", "70000")), RunMode.Dev).Validate();
        Assert.Contains(errors, e => e.StartsWith("Port", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RunMode.Service, ServiceOptions.ProductionPort)]
    [InlineData(RunMode.User, ServiceOptions.ProductionPort)]
    [InlineData(RunMode.Dev, ServiceOptions.DevPort)]
    public void Port_FromSettingsFiles_IsIgnored(RunMode mode, int expected)
    {
        // The updater, install script and CLI all assume 47811: a port in appsettings.json must not move the service.
        var o = ServiceOptions.Resolve(Config(("Port", "50000"), ("RetentionHours", "48")), Config(), mode);
        Assert.Equal(expected, o.Port);
        Assert.Equal(48, o.RetentionHours);
    }

    [Fact]
    public void Port_FromCommandLine_IsUsed()
    {
        var o = ServiceOptions.Resolve(Config(("Port", "50001")), Config(("Port", "50001")), RunMode.Service);
        Assert.Equal(50001, o.Port);
    }

    [Fact]
    public void ServiceMode_LogsInformationToTheApplicationEventLog_AsProcessTracker()
    {
        using var app = new TestApp(DateTime.UtcNow); // no --console: service mode

        var settings = app.Services.GetRequiredService<IOptions<EventLogSettings>>().Value;
        Assert.Equal("ProcessTracker", settings.SourceName);
        Assert.Equal("Application", settings.LogName);
        var rules = app.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules
            .Where(r => r.ProviderName == typeof(EventLogLoggerProvider).FullName).ToList();
        Assert.Contains(rules, r => r.CategoryName is null && r.LogLevel == LogLevel.Information);
        Assert.Contains(rules, r => r.CategoryName == "Microsoft.AspNetCore" && r.LogLevel == LogLevel.Warning);
    }

    [Theory]
    [InlineData("http://127.0.0.1:47811", true)]
    [InlineData("http://[::1]:47811", true)]
    [InlineData("http://localhost:47811", true)]
    [InlineData("http://0.0.0.0:47811", false)]
    [InlineData("http://[::]:47811", false)]
    [InlineData("http://192.168.1.10:47811", false)]
    [InlineData("http://*:47811", false)]
    [InlineData("http://+:47811", false)]
    public void LoopbackGuard_ClassifiesServerAddresses(string address, bool loopback) =>
        Assert.Equal(loopback, LoopbackGuard.IsLoopback(address));

    // A takeover waits for the old user-mode instance to stop; its stop must end well within that wait (ruling R8).

    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static TimeSpan ShutdownTimeout(TestApp app) =>
        app.Services.GetRequiredService<IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value.ShutdownTimeout;

    [Fact]
    public async Task UserMode_ShutdownTimeoutIs15Seconds()
    {
        await using var app = new TestApp(Now, RunMode.User);
        Assert.Equal(TimeSpan.FromSeconds(15), ShutdownTimeout(app));
    }

    [Fact]
    public async Task DevMode_ShutdownTimeoutIsTheDefault()
    {
        await using var app = new TestApp(Now);
        Assert.Equal(new Microsoft.Extensions.Hosting.HostOptions().ShutdownTimeout, ShutdownTimeout(app));
    }

    [Fact]
    public void TakeoverWait_IsTwoMinutes() => Assert.Equal(TimeSpan.FromMinutes(2), SingleInstance.TakeoverWait);
}
