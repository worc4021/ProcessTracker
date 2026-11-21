using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Primitives;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Service;
using ProcessTracker.Service.Api;
using ProcessTracker.Service.Mcp;

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
    // (production only), then the command line. Port comes from the command line only (see ServiceOptions.Port).
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
    try { options = ServiceOptions.Resolve(config, mode, dev); }
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

    if (!asConsole)
    {
        // As a service, Information and above go to the Application event log under the source the MSI creates
        // (Get-WinEvent -ProviderName ProcessTracker). Per-request framework logging stays at Warning.
        builder.Services.AddWindowsService(o => o.ServiceName = "ProcessTracker");
        builder.Services.Configure<EventLogSettings>(s =>
        {
            s.SourceName = "ProcessTracker";
            s.LogName = "Application";
        });
        builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter<EventLogLoggerProvider>("ModelContextProtocol", LogLevel.Warning);
    }
    else
    {
        builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning); // console: the console has the rest
    }
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
    builder.Services.AddSingleton<ICallerResolver, CallerResolver>();
    builder.Services.AddSingleton<OverviewCache>();
    builder.Services.AddHttpContextAccessor(); // MCP tools resolve the caller from the current request
    builder.Services.AddMcpServer(o => o.ServerInstructions = ProcessTrackerTools.ServerInstructions)
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<ProcessTrackerTools>(ContractJson.Options); // same JSON contract as REST (enums as strings)
    builder.Services.AddHostedService<CollectorWorker>();

    var app = builder.Build();
    if (ServiceOptions.HasIgnoredPort(config, mode))
        app.Logger.LogWarning("Port in appsettings.json is ignored; listening on {Port} (only --Port on the command line changes it)",
            options.Port);

    // DNS-rebinding protection (ruling R7): a web page may get a browser to send requests to 127.0.0.1 under its own
    // host name, so only loopback Host names on our port are served, and a browser Origin must be ourselves.
    // Requests without an Origin (the CLI, Claude Code) are fine. Runs before every endpoint, /api and /mcp alike.
    app.Use((ctx, next) =>
    {
        if (LoopbackGuard.IsLocalRequest(ctx.Request.Host, ctx.Request.Headers.Origin, options.Port)) return next(ctx);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    });

    app.UseDashboardHeaders();
    app.MapRestEndpoints();
    app.MapDashboard();
    app.MapMcp("/mcp");

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

    /// <summary>
    /// True when the Host header is <c>127.0.0.1:port</c> or <c>localhost:port</c> and there is either no Origin header
    /// or exactly one that is <c>http://127.0.0.1:port</c> or <c>http://localhost:port</c>.
    /// </summary>
    public static bool IsLocalRequest(HostString host, StringValues origin, int port)
    {
        if (host.Port != port || !IsLoopbackName(host.Host)) return false;
        return origin.Count switch
        {
            0 => true,
            1 => origin[0] is { } o && (o.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                                        || o.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static bool IsLoopbackName(string host) =>
        host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

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
