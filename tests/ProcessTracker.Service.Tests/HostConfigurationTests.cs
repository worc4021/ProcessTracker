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
        ServiceOptions.Resolve(Config(settings), Config(), dev: true);

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

    [Fact]
    public void OutOfRangePort_OnCommandLine_IsRejected()
    {
        var errors = ServiceOptions.Resolve(Config(("Port", "70000")), Config(("Port", "70000")), dev: true).Validate();
        Assert.Contains(errors, e => e.StartsWith("Port", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, ServiceOptions.ProductionPort)]
    [InlineData(true, ServiceOptions.DevPort)]
    public void Port_FromSettingsFiles_IsIgnored(bool dev, int expected)
    {
        // The updater, install script and CLI all assume 47811: a port in appsettings.json must not move the service.
        var o = ServiceOptions.Resolve(Config(("Port", "50000"), ("RetentionHours", "48")), Config(), dev);
        Assert.Equal(expected, o.Port);
        Assert.Equal(48, o.RetentionHours);
    }

    [Fact]
    public void Port_FromCommandLine_IsUsed()
    {
        var o = ServiceOptions.Resolve(Config(("Port", "50001")), Config(("Port", "50001")), dev: false);
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
}
