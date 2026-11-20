using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class DashboardTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string UnitA = "host:local_a";

    private readonly TestApp _app = new(T0);
    private readonly HttpClient _http;

    public DashboardTests() => _http = _app.CreateLocalClient();

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    private static ProcessKey Key(int pid) => new(pid, 1_000_000 + pid);

    private static ProcessView Proc(int pid, string image, string? unit, int? parentPid = null, ProcessState state = ProcessState.Normal)
    {
        var key = Key(pid);
        var attribution = unit is null ? Attribution.Unattributed : new Attribution(AttributionKind.Session, unit, null, false);
        var record = new ProcessRecord(key, parentPid is { } p ? Key(p) : null, image, null, null, attribution, T0, null);
        var sample = new ProcessSample(key, T0, 0.5f, 100, 100, 0, 0, 0, 0, 0, 0, 0, 10, 2);
        return new ProcessView(record, sample, default, null, state, false);
    }

    private static SessionView Session(string unit) =>
        new(new SessionInfo(unit, null, [], null, T0, null, @"C:\work", "a title", null), true, 1f, 100, 1);

    [Fact]
    public async Task Root_RedirectsToDashboard()
    {
        using var client = _app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"http://localhost:{_app.Port()}"),
        });

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Index_IsHtmlWithSecurityHeaders()
    {
        using var response = await _http.GetAsync("/dashboard/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(
            "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; "
            + "base-uri 'none'; frame-ancestors 'none'; form-action 'none'", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("dashboard.js", html);
        Assert.DoesNotContain("<script>", html); // CSP forbids inline scripts
        Assert.DoesNotContain("style=", html);
    }

    [Theory]
    [InlineData("/dashboard/dashboard.js", "text/javascript")]
    [InlineData("/dashboard/dashboard.css", "text/css")]
    public async Task Assets_AreServedWithTypeAndHeaders(string path, string mediaType)
    {
        using var response = await _http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/dashboard/nope.js")]
    [InlineData("/dashboard/..%5Cappsettings.json")]
    [InlineData("/dashboard/a/b")]
    public async Task UnknownFile_Is404(string path)
    {
        using var response = await _http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard/")]
    [InlineData("/dashboard/dashboard.js")]
    public async Task WrongHost_Is403(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://evil.example:{_app.Port()}{path}");

        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Overview_HasLoadSessionTreesAndOrphansFromOneView()
    {
        _app.Tracker.View = new TrackerView(T0, default,
            new[]
            {
                Proc(10, "claude.exe", UnitA),
                Proc(11, "node.exe", UnitA, parentPid: 10),
                Proc(12, "Dymola.exe", UnitA, state: ProcessState.Orphan),
                Proc(20, "claude.exe", "host:local_b"),
            }.ToFrozenDictionary(p => p.Record.Key),
            [Session(UnitA), Session("host:local_b")]);

        var o = await _http.GetFromJsonAsync<OverviewDto>("/api/overview", ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(o);
        Assert.Equal(T0, o.TimestampUtc);
        Assert.NotNull(o.Load);
        Assert.Equal(2, o.Sessions.Count);
        var a = o.Sessions.Single(s => s.Session.UnitId == UnitA);
        Assert.Equal([Key(10).ToString(), Key(12).ToString()], a.Processes.Select(p => p.Process.Key));
        Assert.Equal(Key(11).ToString(), Assert.Single(a.Processes[0].Children).Process.Key);
        Assert.Equal(Key(12).ToString(), Assert.Single(o.Orphans).Key);
    }
}
