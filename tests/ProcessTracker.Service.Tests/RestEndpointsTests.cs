using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class RestEndpointsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string UnitA = "host:local_a";

    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public RestEndpointsTests() => _http = _app.CreateLocalClient();

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    /// <summary>The real Program with the tracker, clock and collector replaced. Dev mode: no single-instance mutex.</summary>
    private sealed class TestApp : WebApplicationFactory<Program>
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
                s.AddSingleton<TimeProvider>(new FixedTimeProvider(T0));
                foreach (var d in s.Where(d => d.ImplementationType == typeof(CollectorWorker)).ToList()) s.Remove(d);
            });
        }
    }

    private static ProcessKey Key(int pid) => new(pid, 1_000_000 + pid);

    private static ProcessView Proc(
        int pid, string image, string? unit, int? parentPid = null,
        ProcessState state = ProcessState.Normal, bool alertable = false, long privateBytes = 0)
    {
        var key = Key(pid);
        var attribution = unit is null ? Attribution.Unattributed : new Attribution(AttributionKind.Session, unit, null, false);
        var record = new ProcessRecord(key, parentPid is { } p ? Key(p) : null, image, null, null, attribution, T0, null);
        var sample = new ProcessSample(key, T0, 0.5f, privateBytes, privateBytes, 0, 0, 0, 0, 0, 0, 0, 10, 2);
        return new ProcessView(record, sample, default, null, state, alertable);
    }

    private static SessionView Session(string unit, string title = "a title") =>
        new(new SessionInfo(unit, null, [], null, T0, null, @"C:\work", title, null), true, 1f, 100, 1);

    private static TrackerView View(DateTime ts, IEnumerable<ProcessView> processes, params SessionView[] sessions) =>
        new(ts, default, processes.ToFrozenDictionary(p => p.Record.Key), sessions);

    [Fact]
    public async Task Health_ReportsVersionAndTickAge()
    {
        _app.Tracker.StartedUtc = T0.AddHours(-1);
        _app.Tracker.View = View(T0.AddSeconds(-3), []);

        var health = await _http.GetFromJsonAsync<HealthDto>("/api/health", ContractJson.Options, TestContext.Current.CancellationToken);

        var expected = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.NotNull(health);
        Assert.Equal(expected, health.Version);
        Assert.Equal(T0.AddHours(-1), health.StartedUtc);
        Assert.Equal(T0.AddSeconds(-3), health.LastTickUtc);
        Assert.Equal(3.0, health.TickAgeSeconds!.Value, 3);
    }

    [Fact]
    public async Task Health_ReportsMode()
    {
        var json = await _http.GetStringAsync("/api/health", TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dev", doc.RootElement.GetProperty("mode").GetString());
        Assert.False(doc.RootElement.TryGetProperty("owner", out _), json);
    }

    [Fact]
    public async Task Orphans_OnlyOrphansAlertableFirst()
    {
        _app.Tracker.View = View(T0,
        [
            Proc(1, "MATLAB.exe", UnitA, state: ProcessState.Orphan, privateBytes: 100),
            Proc(2, "MATLAB.exe", UnitA, state: ProcessState.Orphan, alertable: true, privateBytes: 10),
            Proc(3, "node.exe", UnitA, state: ProcessState.Orphan, privateBytes: 500),
            Proc(4, "claude.exe", UnitA, state: ProcessState.Normal, privateBytes: 9999),
            Proc(5, "cmd.exe", UnitA, state: ProcessState.Detached, privateBytes: 9999),
        ]);

        var orphans = await _http.GetFromJsonAsync<ProcessViewDto[]>("/api/orphans", ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(orphans);
        Assert.Equal([Key(2).ToString(), Key(3).ToString(), Key(1).ToString()], orphans.Select(o => o.Key));
        Assert.All(orphans, o => Assert.Equal(ProcessState.Orphan, o.State));
    }

    [Fact]
    public async Task SessionTree_NestsChildren()
    {
        _app.Tracker.View = View(T0,
        [
            Proc(10, "claude.exe", UnitA),
            Proc(11, "node.exe", UnitA, parentPid: 10),
            Proc(12, "MATLAB.exe", UnitA, parentPid: 11),
            Proc(13, "cmd.exe", UnitA, parentPid: 10),
            Proc(20, "claude.exe", "host:local_b"),
            Proc(21, "node.exe", "host:local_b", parentPid: 10), // foreign process, even with a parent in the tree
        ], Session(UnitA), Session("host:local_b"));

        var tree = await _http.GetFromJsonAsync<SessionTreeDto>($"/api/sessions/{UnitA}", ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(tree);
        Assert.Equal(UnitA, tree.Session.UnitId);
        var root = Assert.Single(tree.Processes);
        Assert.Equal(Key(10).ToString(), root.Process.Key);
        Assert.Equal([Key(11).ToString(), Key(13).ToString()], root.Children.Select(c => c.Process.Key));
        var grandchild = Assert.Single(root.Children[0].Children);
        Assert.Equal(Key(12).ToString(), grandchild.Process.Key);
        Assert.Empty(grandchild.Children);
        Assert.Empty(root.Children[1].Children);
    }

    [Fact]
    public async Task History_DefaultsToLast30Minutes()
    {
        _app.Tracker.View = View(T0, [], Session(UnitA, "my session"));
        SessionRec Agg(string unit, DateTime ts, float cpu) => new(new SessionAggregate(unit, ts, cpu, 1000, 1, 2, 3));
        _app.Tracker.Records.AddRange(
        [
            Agg(UnitA, T0.AddMinutes(-40), 9),
            Agg(UnitA, T0.AddMinutes(-20), 1),
            Agg("host:local_b", T0.AddMinutes(-15), 7),
            Agg(UnitA, T0.AddMinutes(-10), 2),
        ]);

        var series = await _http.GetFromJsonAsync<HistorySeriesDto>($"/api/history?unit={UnitA}", ContractJson.Options, TestContext.Current.CancellationToken);

        var call = Assert.Single(_app.Tracker.HistoryCalls);
        Assert.Equal((T0.AddMinutes(-30), T0), call);
        Assert.NotNull(series);
        Assert.Equal(UnitA, series.UnitId);
        Assert.Equal("my session", series.Name);
        Assert.Equal(T0.AddMinutes(-30), series.FromUtc);
        Assert.Equal(T0, series.ToUtc);
        Assert.Equal([T0.AddMinutes(-20), T0.AddMinutes(-10)], series.Points.Select(p => p.TimestampUtc));
        Assert.Equal([1f, 2f], series.Points.Select(p => p.CpuCores));
        Assert.All(series.Points, p => Assert.Equal(3, p.ProcessCount));
    }

    [Fact]
    public async Task UnknownSession_404()
    {
        _app.Tracker.View = View(T0, [Proc(10, "claude.exe", UnitA)], Session(UnitA));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/sessions/host:local_nope", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/history?unit=host:local_nope", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/processes/10@1", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/api/sessions/{UnitA}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/api/processes/{Key(10)}", ct)).StatusCode);
    }

    [Fact]
    public async Task AuditKill_IsRecorded()
    {
        var dto = new KillAuditDto("1234@5678", "MATLAB.exe", UnitA, "proctrack.exe", Tree: true, Result: "killed");

        var response = await _http.PostAsJsonAsync("/api/audit/kill", dto, ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var kill = Assert.Single(_app.Tracker.Kills);
        Assert.Equal(new KillAudit(T0, new ProcessKey(1234, 5678), "MATLAB.exe", UnitA, "proctrack.exe", true, "killed"), kill);
    }

    [Fact]
    public async Task History_RangeBeforeRetention_IsEmptyNot500()
    {
        _app.Tracker.View = View(T0, [], Session(UnitA));
        var ct = TestContext.Current.CancellationToken;

        var response = await _http.GetAsync($"/api/history?unit={UnitA}&to=0001-01-01T00:00:00Z", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var series = await response.Content.ReadFromJsonAsync<HistorySeriesDto>(ContractJson.Options, ct);
        Assert.NotNull(series);
        Assert.Empty(series.Points);
        Assert.Equal(T0.AddHours(-24), series.FromUtc);
        Assert.Equal(T0.AddHours(-24), series.ToUtc);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/history?key=1@2&to=0001-01-01T00:10:00Z", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.GetAsync($"/api/history?unit={UnitA}&from=2026-11-20T12:00:00Z&to=2026-11-20T11:00:00Z", ct)).StatusCode);
    }

    [Fact]
    public Task ListensOnLoopbackOnly() =>
        RunRealHostAsync([], allowExit: false, (lan, port) => AssertRefusedAsync(lan, port));

    [Fact]
    public Task ConfiguredKestrelEndpoint_CannotWidenBinding()
    {
        var extra = FreePort();
        return RunRealHostAsync([$"--Kestrel:Endpoints:Lan:Url=http://0.0.0.0:{extra}"], allowExit: true, async (lan, port) =>
        {
            await AssertRefusedAsync(lan, port);
            await AssertRefusedAsync(lan, extra);
        });
    }

    [Fact]
    public Task HostingUrls_CannotWidenBinding()
    {
        var extra = FreePort();
        return RunRealHostAsync(["--urls", $"http://0.0.0.0:{extra}", "--preferHostingUrls", "true"], allowExit: true,
            async (lan, port) =>
            {
                await AssertRefusedAsync(lan, port);
                await AssertRefusedAsync(lan, extra);
            });
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task AssertRefusedAsync(IPAddress lan, int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var refused = await Assert.ThrowsAsync<SocketException>(
            async () => await socket.ConnectAsync(new IPEndPoint(lan, port), TestContext.Current.CancellationToken));
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    /// <summary>
    /// Starts the real exe with --console --dev on a free port and a temp data dir, waits until 127.0.0.1 answers,
    /// then runs <paramref name="assertLan"/> against the machine's first LAN IPv4 address (skipped, with a message,
    /// when there is none). With <paramref name="allowExit"/>, a host that refuses to run (non-zero exit) also passes.
    /// </summary>
    private static async Task RunRealHostAsync(string[] extraArgs, bool allowExit, Func<IPAddress, int, Task> assertLan)
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var exe = Path.Combine(AppContext.BaseDirectory, "ProcessTracker.Service.exe");
        var dataDir = Path.Combine(Path.GetTempPath(), "ProcessTracker-test-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["--console", "--dev", "--DataDir", dataDir, "--Port", port.ToString(), .. extraArgs])
            psi.ArgumentList.Add(a);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = Process.Start(psi)!;
        host.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        host.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var answered = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!answered && DateTime.UtcNow < deadline && !host.HasExited)
            {
                try { answered = (await http.GetAsync($"http://127.0.0.1:{port}/api/health", ct)).IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { } // probe timed out
                if (!answered) await Task.Delay(250, ct);
            }
            if (allowExit && host.HasExited)
            {
                Assert.NotEqual(0, host.ExitCode);
                return;
            }
            Assert.True(answered, "127.0.0.1 did not answer. Host output:\n" + string.Join("\n", output));
            Assert.False(host.HasExited, "the dev host exited. Host output:\n" + string.Join("\n", output));

            var lan = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (lan is null)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("No non-loopback IPv4 address on this machine; LAN refusal not checked.");
                return;
            }

            await assertLan(lan, port);
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(CancellationToken.None);
            for (var i = 0; i < 20 && Directory.Exists(dataDir); i++)
            {
                try { Directory.Delete(dataDir, recursive: true); }
                catch (IOException) { await Task.Delay(100, CancellationToken.None); }
            }
        }
    }
}
