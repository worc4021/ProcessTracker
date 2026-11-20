using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ProcessTracker.Service.Tests.Fakes;

/// <summary>The real Program with the tracker, clock and collector replaced. Dev mode: no single-instance mutex.</summary>
public sealed class TestApp(DateTime utcNow) : WebApplicationFactory<Program>
{
    public FakeTrackerHost Tracker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dev", "true");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITrackerHost>();
            s.AddSingleton<ITrackerHost>(Tracker);
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
            foreach (var d in s.Where(d => d.ImplementationType == typeof(CollectorWorker)).ToList()) s.Remove(d);
        });
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
