using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Primitives;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Native;
using ProcessTracker.Service;
using ProcessTracker.Service.Api;
using ProcessTracker.Service.Logging;
using ProcessTracker.Service.Mcp;

// Flags: --console (run as a console app instead of a Windows service), --dev (port 47812, dev data dir, no
// single-instance mutex) and --user (run as the logged-in user, with data, settings and a log file in
// %LOCALAPPDATA%\ProcessTracker). They are also accepted as configuration keys Console/Dev/User, e.g. "--Dev=true".
// With --user, --takeover <previousAppDir> asks the running user-mode instance to exit and waits up to 30 s for it.
// Exit codes: 0 stopped normally, 1 invalid configuration, 2 another instance is running (or holds the exit event),
// 3 the server ended up listening on a non-loopback address, 4 user mode refused: the machine service is installed.
var commandLine = new ConfigurationBuilder().AddCommandLine(RunModes.NormalizeFlags(args)).Build();

// The exe is a GUI-subsystem app (no window at logon in user mode); --console gets a console before anything logs.
var asConsole = bool.TryParse(commandLine["Console"], out var consoleFlag) && consoleFlag;
if (asConsole) ConsoleWindow.Attach();

RunMode runMode;
try { runMode = RunModes.ParseMode(commandLine); }
catch (InvalidOperationException e) // --user with --dev, or a flag value that is not a bool
{
    Console.Error.WriteLine("Invalid configuration: " + e.Message);
    return 1;
}

// The API is unauthenticated and runs as SYSTEM, so no configuration may add or replace listeners. The command line
// is re-emitted without listener keys (the host reads urls etc. from the arguments it is created with), a last
// configuration source blanks them for the JSON files, Kestrel's own "Kestrel" section is ignored, and a startup
// check stops the host if it is listening anywhere but loopback anyway.
var hostArgs = commandLine.AsEnumerable()
    .Where(kv => kv.Value is not null && !LoopbackGuard.IsListenerKey(kv.Key))
    .Select(kv => $"--{kv.Key}={kv.Value}")
    .ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

// Configuration: appsettings.json next to the exe, then the mode's data folder's appsettings.json
// (%ProgramData%\ProcessTracker, or %LOCALAPPDATA%\ProcessTracker in user mode; none in dev mode), then the command
// line. Port comes from the command line only (see ServiceOptions.Port).
var config = builder.Configuration;
config.Sources.Clear();
config.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
if (runMode != RunMode.Dev)
{
    config.AddJsonFile(Path.Combine(ServiceOptions.DefaultDataDir(runMode), "appsettings.json"),
        optional: true, reloadOnChange: false);
}
config.AddCommandLine(hostArgs);
config.AddInMemoryCollection(LoopbackGuard.NeutralizedHostingKeys);

ServiceOptions options;
try { options = ServiceOptions.Resolve(config, commandLine, runMode); }
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
var logDir = Path.Combine(options.DataDir, "logs");

// The machine service always wins: a user-mode instance does not start while it is installed, running or not.
var instanceChecks = runMode != RunMode.Dev && !Program.SkipInstanceChecks;
if (instanceChecks && runMode == RunMode.User && ServiceProbe.IsMachineServiceInstalled())
{
    using var log = new FileLoggerProvider(logDir);
    log.CreateLogger("ProcessTracker").LogInformation(
        "The machine-wide ProcessTracker service is installed; this user-mode instance exits (code 4)");
    return 4;
}

// --takeover <previousAppDir> (user mode): a newer version replacing a running one asks it to exit through the exit
// event, then waits for it to release the mutex.
var takeover = runMode == RunMode.User && !string.IsNullOrEmpty(commandLine["Takeover"]);
if (instanceChecks && takeover)
{
    try { UserModeSignal.TrySignal(); }
    catch (UnauthorizedAccessException) { } // another user's instance: the mutex wait below fails and says so
}

using var mutex = instanceChecks ? SingleInstance.TryAcquire(takeover ? SingleInstance.TakeoverWait : TimeSpan.Zero) : null;
if (instanceChecks && mutex is null)
{
    Console.Error.WriteLine(@"Another ProcessTracker instance holds Global\ProcessTracker.Service; exiting.");
    return 2;
}

try
{
    // User mode: the exit event the MSI (before starting the machine service) or a newer version sets to stop this
    // instance gracefully. Created only once the mutex is ours, so a stale signal for the previous instance is reset.
    using var exitEvent = instanceChecks && runMode == RunMode.User ? CreateExitEvent() : null;
    if (instanceChecks && runMode == RunMode.User && exitEvent is null) return 2;

    if (runMode == RunMode.User)
    {
        // User mode: no event log (creating its source needs admin) and no Windows service host. Information and above
        // go to a rolling file in the data folder, and to the console with --console. Per-request framework logging
        // stays at Warning. Registered through a factory so the host disposes it (closing the file) on shutdown.
        builder.Logging.ClearProviders();
        if (asConsole) builder.Logging.AddConsole();
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(logDir));
        builder.Logging.AddFilter<FileLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter<FileLoggerProvider>("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter<FileLoggerProvider>("ModelContextProtocol", LogLevel.Warning);
    }
    else if (!asConsole)
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
    builder.Services.AddSingleton<IRequestOwnerGate, OwnerGate>();
    builder.Services.AddSingleton<OverviewCache>();
    builder.Services.AddHttpContextAccessor(); // MCP tools resolve the caller from the current request
    builder.Services.AddMcpServer(o => o.ServerInstructions = ProcessTrackerTools.ServerInstructions)
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<ProcessTrackerTools>(ContractJson.Options); // same JSON contract as REST (enums as strings)
    builder.Services.AddHostedService<CollectorWorker>();
    if (exitEvent is not null)
    {
        builder.Services.AddHostedService(sp => new UserModeExitWatcher(exitEvent,
            sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<UserModeExitWatcher>>()));
    }

    var app = builder.Build();
    if (ServiceOptions.HasIgnoredPort(config, commandLine))
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

    // User mode serves only its owner: everything but GET /api/health (so a client can see whose instance this is)
    // must come from a process running as this user. Runs before routing, so a rejected request's body is never read.
    if (runMode == RunMode.User)
    {
        app.Use((ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method)
                && ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
                return next(ctx);
            if (ctx.RequestServices.GetRequiredService<IRequestOwnerGate>().IsOwner(ctx)) return next(ctx);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ctx.Response.WriteAsync(OwnerGate.ForbiddenMessage);
        });
    }

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
        app.Logger.LogInformation("ProcessTracker {Version} listening on {Addresses}, data in {DataDir}, GC heap limit {HeapMb} MB, {Mode} mode",
            RestEndpoints.Version, string.Join(", ", addresses), options.DataDir,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20, runMode.WireName());
    });

    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
    app.Run(); // synchronous, so the mutex is released on the thread that acquired it
    return exposed ? 3 : 0;
}
finally
{
    mutex?.ReleaseMutex();
}

static EventWaitHandle? CreateExitEvent()
{
    try { return UserModeSignal.CreateForInstance(); }
    catch (UnauthorizedAccessException e)
    {
        Console.Error.WriteLine($"Cannot create the user-mode exit event: {e.Message} Exiting.");
        return null;
    }
}

/// <summary>The machine-wide single-instance guard (not taken with --dev).</summary>
internal static class SingleInstance
{
    /// <summary>How long a --takeover start waits for the instance it replaces to exit.</summary>
    public static readonly TimeSpan TakeoverWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The owned mutex, or null when another instance (possibly under another account) still holds it after
    /// <paramref name="wait"/>.
    /// </summary>
    public static Mutex? TryAcquire(TimeSpan wait, string name = UserModeSignal.InstanceMutexName)
    {
        Mutex m;
        try { m = new Mutex(initiallyOwned: false, name); }
        catch (UnauthorizedAccessException) { return null; } // created by an instance under another account
        try
        {
            if (m.WaitOne(wait)) return m;
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

/// <summary>
/// The exe is a GUI-subsystem app so that user mode shows no window at logon. With --console it borrows the parent's
/// console, or opens its own, so log lines still appear. Output that is already redirected to a pipe or file (a test or
/// script capturing it) is left as it is.
/// </summary>
internal static class ConsoleWindow
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const uint FileTypeDisk = 1, FileTypePipe = 3;

    public static void Attach()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (stdout is not (0 or -1) && GetFileType(stdout) is FileTypeDisk or FileTypePipe) return;
        if (!AttachConsole(AttachParentProcess)) AllocConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}

public partial class Program
{
    /// <summary>
    /// Test seam (ruling R1), set only by the test host: skips the single-instance mutex and the machine-service probe,
    /// so tests never contend with an installed ProcessTracker service. Deliberately not a configuration key.
    /// </summary>
    internal static bool SkipInstanceChecks { get; set; }
}
