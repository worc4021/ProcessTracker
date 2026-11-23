using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcessTracker.Core.Hooks;

namespace ProcessTracker.Service.Tests.Fakes;

/// <summary>
/// The real Program with the tracker, clock and collector replaced. Dev mode by default. In every mode the
/// single-instance mutex and the machine-service probe are skipped (ruling R1), so tests never contend with an
/// installed ProcessTracker service. User mode gets a temporary data folder (its log file goes there).
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly DateTime _utcNow;
    private readonly RunMode _mode;
    private readonly string? _dataDir;

    public TestApp(DateTime utcNow, RunMode mode = RunMode.Dev)
    {
        Program.SkipInstanceChecks = true;
        _utcNow = utcNow;
        _mode = mode;
        if (mode == RunMode.User)
            _dataDir = Path.Combine(Path.GetTempPath(), "ProcessTracker-test-" + Guid.NewGuid().ToString("N"));
    }

    public FakeTrackerHost Tracker { get; } = new();

    /// <summary>Replaces the owner gate when set; null keeps the production one.</summary>
    public IRequestOwnerGate? OwnerGate { get; init; }

    /// <summary>Replaces the last-input probe when set; null keeps the mode's own.</summary>
    public IUserActivity? UserActivity { get; init; }

    /// <summary>The temporary data folder in user mode; null otherwise.</summary>
    public string? DataDir => _dataDir;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        switch (_mode)
        {
            case RunMode.Dev: builder.UseSetting("Dev", "true"); break;
            case RunMode.User: builder.UseSetting("User", "true"); break;
        }
        if (_dataDir is not null) builder.UseSetting("DataDir", _dataDir);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITrackerHost>();
            s.AddSingleton<ITrackerHost>(Tracker);
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new FixedTimeProvider(_utcNow));
            if (OwnerGate is not null)
            {
                s.RemoveAll<IRequestOwnerGate>();
                s.AddSingleton(OwnerGate);
            }
            if (UserActivity is not null)
            {
                s.RemoveAll<IUserActivity>();
                s.AddSingleton(UserActivity);
            }
            foreach (var d in s.Where(d => d.ImplementationType == typeof(CollectorWorker)).ToList()) s.Remove(d);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync(); // closes the log file
        if (_dataDir is not null)
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public static class TestAppExtensions
{
    /// <summary>The service's configured port: the only one its Host/Origin guard accepts.</summary>
    public static int Port(this WebApplicationFactory<Program> app) => app.Services.GetRequiredService<ServiceOptions>().Port;

    /// <summary>
    /// A client for the in-memory server whose requests carry <c>Host: localhost:&lt;port&gt;</c>, as a real loopback
    /// client's would (the default base address has no port, which the DNS-rebinding guard rejects).
    /// </summary>
    public static HttpClient CreateLocalClient(this WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://localhost:{app.Port()}") });
}
