using Microsoft.Extensions.Configuration;

namespace ProcessTracker.Service.Tests;

public sealed class HostConfigurationTests
{
    private static ServiceOptions Resolve(params (string Key, string Value)[] settings) =>
        ServiceOptions.Resolve(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build(),
            dev: true);

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
    [InlineData("Port", "70000")]
    public void OutOfRangeValues_AreRejected(string key, string value)
    {
        var errors = Resolve((key, value)).Validate();
        Assert.Contains(errors, e => e.StartsWith(key, StringComparison.Ordinal));
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
