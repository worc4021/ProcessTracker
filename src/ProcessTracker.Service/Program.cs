using System.Diagnostics;
using System.Net;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Service;
using ProcessTracker.Service.Api;

// Flags: --console (run as a console app instead of a Windows service) and --dev (port 47812, dev data dir,
// no single-instance mutex). Both are also accepted as configuration keys Console/Dev, e.g. "--Dev=true".
var configArgs = args.Select(a => a switch
{
    "--dev" => "--Dev=true",
    "--console" => "--Console=true",
    _ => a,
}).ToArray();
var mode = new ConfigurationBuilder().AddCommandLine(configArgs).Build();
var dev = mode.GetValue<bool>("Dev");
var console = mode.GetValue<bool>("Console");

using var mutex = dev ? null : SingleInstance.TryAcquire();
if (!dev && mutex is null)
{
    Console.Error.WriteLine(@"Another ProcessTracker instance holds Global\ProcessTracker.Service; exiting.");
    return 2;
}

try
{
    var builder = WebApplication.CreateBuilder(configArgs);

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
    config.AddCommandLine(configArgs);

    var options = ServiceOptions.Resolve(config, dev);

    if (!console) builder.Services.AddWindowsService(o => o.ServiceName = "ProcessTracker");
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
    builder.Services.ConfigureHttpJsonOptions(o => ContractJson.Configure(o.SerializerOptions));

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<TrackerHost>();
    builder.Services.AddSingleton<ITrackerHost>(sp => sp.GetRequiredService<TrackerHost>());
    builder.Services.AddSingleton<ICallerResolver, NullCallerResolver>();
    builder.Services.AddHostedService<CollectorWorker>();

    var app = builder.Build();
    app.MapRestEndpoints();

    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
    app.Logger.LogInformation("ProcessTracker {Version} listening on 127.0.0.1:{Port}, data in {DataDir}, GC heap limit {HeapMb} MB{Dev}",
        RestEndpoints.Version, options.Port, options.DataDir, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20, dev ? " (dev)" : "");
    app.Run(); // synchronous, so the mutex is released on the thread that acquired it
    return 0;
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

public partial class Program;
