using System.Net.Http.Json;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Native;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class RunModeTests
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    private static RunMode Parse(params string[] args) =>
        RunModes.ParseMode(new ConfigurationBuilder().AddCommandLine(RunModes.NormalizeFlags(args)).Build());

    [Fact]
    public void Resolve_UserMode_UsesLocalAppDataAndProductionPort()
    {
        var o = ServiceOptions.Resolve(Empty, Empty, RunMode.User);

        Assert.Equal(RunMode.User, o.Mode);
        Assert.Equal(47811, o.Port);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker"),
            o.DataDir);
    }

    [Fact]
    public void Resolve_ServiceMode_UsesProgramData()
    {
        var o = ServiceOptions.Resolve(Empty, Empty, RunMode.Service);

        Assert.Equal(RunMode.Service, o.Mode);
        Assert.Equal(47811, o.Port);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker"),
            o.DataDir);
    }

    [Fact]
    public void Resolve_DevMode_Unchanged()
    {
        var o = ServiceOptions.Resolve(Empty, Empty, RunMode.Dev);

        Assert.Equal(RunMode.Dev, o.Mode);
        Assert.Equal(47812, o.Port);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessTracker-dev"),
            o.DataDir);
    }

    [Fact]
    public void Resolve_ModeInSettingsFile_IsIgnored()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Mode", "Dev")]).Build();
        Assert.Equal(RunMode.User, ServiceOptions.Resolve(config, Empty, RunMode.User).Mode);
    }

    [Fact]
    public void ParseMode_UserFlag()
    {
        Assert.Equal(RunMode.User, Parse("--user"));
        Assert.Equal(RunMode.Dev, Parse("--console", "--dev"));
        Assert.Equal(RunMode.Service, Parse());
        Assert.Equal(RunMode.Service, Parse("--console"));
    }

    [Fact]
    public void ParseMode_UserAndDev_IsError() =>
        Assert.Throws<InvalidOperationException>(() => Parse("--user", "--dev"));

    [Theory]
    [InlineData(RunMode.Service, "service")]
    [InlineData(RunMode.User, "user")]
    [InlineData(RunMode.Dev, "dev")]
    public void WireName_IsLowerCase(RunMode mode, string expected) => Assert.Equal(expected, mode.WireName());

    // Hermetic (ruling R1): probes names whose presence does not depend on whether ProcessTracker is installed.
    [Fact]
    public void ServiceProbe_FindsInstalledServicesOnly()
    {
        Assert.True(ServiceProbe.IsInstalled("EventLog"));
        Assert.False(ServiceProbe.IsInstalled("ProcessTracker-NoSuchService-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task UserMode_HealthReportsOwner_AndLogsToFile()
    {
        await using var app = new TestApp(DateTime.UtcNow, RunMode.User);
        using var http = app.CreateLocalClient();

        var health = await http.GetFromJsonAsync<HealthDto>("/api/health", ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(health);
        Assert.Equal("user", health.Mode);
        using (var me = WindowsIdentity.GetCurrent()) Assert.Equal(me.Name, health.Owner);
        Assert.Equal(ServiceOptions.ProductionPort, app.Port());
        var log = Path.Combine(app.DataDir!, "logs", "processtracker.log");
        using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Contains("listening on", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
