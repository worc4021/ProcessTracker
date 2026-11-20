using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Service;
using ProcessTracker.Service.Api;

// Flags: --console (run as a console app instead of a Windows service) and --dev (port 47812, dev data dir,
// no single-instance mutex). Both are also accepted as configuration keys Console/Dev, e.g. "--Dev=true".
// Exit codes: 0 stopped normally, 1 invalid configuration, 2 another instance is running,
// 3 the server ended up listening on a non-loopback address.
var configArgs = args.Select(a => a switch
{
    "--dev" => "--Dev=true",
    "--console" => "--Console=true",
    _ => a,
}).ToArray();
var mode = new ConfigurationBuilder().AddCommandLine(configArgs).Build();

// The API is unauthenticated and runs as SYSTEM, so no configuration may add or replace listeners. The command line
// is re-emitted without listener keys (the host reads urls etc. from the arguments it is created with), a last
// configuration source blanks them for the JSON files, Kestrel's own "Kestrel" section is ignored, and a startup
// check stops the host if it is listening anywhere but loopback anyway.
var hostArgs = mode.AsEnumerable()
    .Where(kv => kv.Value is not null && !LoopbackGuard.IsListenerKey(kv.Key))
    .Select(kv => $"--{kv.Key}={kv.Value}")
    .ToArray();
var dev = mode.GetValue<bool>("Dev");
var asConsole = mode.GetValue<bool>("Console");

using var mutex = dev ? null : SingleInstance.TryAcquire();
if (!dev && mutex is null)
{
    Console.Error.WriteLine(@"Another ProcessTracker instance holds Global\ProcessTracker.Service; exiting.");
    return 2;
}

try
{
    var builder = WebApplication.CreateBuilder(hostArgs);

    // Configuration: appsettings.json next to the exe, then %ProgramData%\ProcessTracker\appsettings.json
    // (production only), then the command line.
    var config = builder.Configuration;
    config.Sources.Clear();
    config.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
    if (!dev)
    {
        config.AddJsonFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessTracker", "appsettings.json"),
            optional: true, reloadOnChange: false);
    }
    config.AddCommandLine(hostArgs);
    config.AddInMemoryCollection(LoopbackGuard.NeutralizedHostingKeys);

    ServiceOptions options;
    try { options = ServiceOptions.Resolve(config, dev); }
    catch (InvalidOperationException e) // a value that does not convert, e.g. RetentionHours=abc
    {
        Console.Error.WriteLine("Invalid configuration: " + e.Message);
        return 1;
    }
    if (options.Validate() is { Count: > 0 } errors)
    {
        Console.Error.WriteLine("Invalid configuration: " + string.Join("; ", errors));
        return 1;
    }

    if (!asConsole) builder.Services.AddWindowsService(o => o.ServiceName = "ProcessTracker");
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false); // drop Kestrel:Endpoints etc.
        k.Listen(IPAddress.Loopback, options.Port);
    });
    builder.Services.ConfigureHttpJsonOptions(o => ContractJson.Configure(o.SerializerOptions));

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<TrackerHost>();
    builder.Services.AddSingleton<ITrackerHost>(sp => sp.GetRequiredService<TrackerHost>());
    builder.Services.AddSingleton<ICallerResolver, NullCallerResolver>();
    builder.Services.AddHostedService<CollectorWorker>();

    var app = builder.Build();
    app.MapRestEndpoints();

    // Fail fast if the server is listening anywhere but loopback, and log what it actually bound.
    var exposed = false;
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                        ?? (ICollection<string>)[];
        if (addresses.FirstOrDefault(a => !LoopbackGuard.IsLoopback(a)) is { } bad)
        {
            exposed = true;
            app.Logger.LogCritical("Listening on non-loopback address {Address} (all: {Addresses}); stopping",
                bad, string.Join(", ", addresses));
            app.Lifetime.StopApplication();
            return;
        }
        app.Logger.LogInformation("ProcessTracker {Version} listening on {Addresses}, data in {DataDir}, GC heap limit {HeapMb} MB{Dev}",
            RestEndpoints.Version, string.Join(", ", addresses), options.DataDir,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20, dev ? " (dev)" : "");
    });

    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
    app.Run(); // synchronous, so the mutex is released on the thread that acquired it
    return exposed ? 3 : 0;
}
finally
{
    mutex?.ReleaseMutex();
}

/// <summary>The machine-wide single-instance guard (not taken with --dev).</summary>
internal static class SingleInstance
{
    private const string Name = @"Global\ProcessTracker.Service";

    /// <summary>The owned mutex, or null when another instance (possibly under another account) holds it.</summary>
    public static Mutex? TryAcquire()
    {
        Mutex m;
        try { m = new Mutex(initiallyOwned: false, Name); }
        catch (UnauthorizedAccessException) { return null; } // created by an instance under another account
        try
        {
            if (m.WaitOne(0)) return m;
        }
        catch (AbandonedMutexException)
        {
            return m; // previous owner died without releasing; we own it now
        }
        m.Dispose();
        return null;
    }
}

/// <summary>Keeps the API on loopback whatever the configuration says.</summary>
public static class LoopbackGuard
{
    /// <summary>Host settings that could add or replace listeners, forced to harmless values.</summary>
    public static IReadOnlyDictionary<string, string?> NeutralizedHostingKeys { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["urls"] = "",
        ["preferHostingUrls"] = "false",
        ["http_ports"] = "",
        ["https_ports"] = "",
    };

    /// <summary>True for configuration keys that configure listeners: the hosting URL keys and Kestrel's section.</summary>
    public static bool IsListenerKey(string key) =>
        NeutralizedHostingKeys.ContainsKey(key)
        || key.Equals("Kestrel", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Kestrel:", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a server address (e.g. "http://127.0.0.1:47811") is a loopback address or "localhost".</summary>
    public static bool IsLoopback(string address)
    {
        string host;
        try { host = BindingAddress.Parse(address).Host; }
        catch (FormatException) { return false; }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}

public partial class Program;
