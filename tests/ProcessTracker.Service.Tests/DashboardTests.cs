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
        // CSP forbids inline script and style: exactly one external script, no handler attributes, no style.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<script", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Matches(@"<script src=""dashboard\.js""></script>", html);
        Assert.DoesNotMatch(@"(?i)\son\w+\s*=", html);
        Assert.DoesNotContain("style=", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void EmbeddedResources_AreExactlyTheThreeDashboardFiles()
    {
        var names = typeof(Program).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("dashboard/")).Order();

        Assert.Equal(["dashboard/dashboard.css", "dashboard/dashboard.js", "dashboard/index.html"], names);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage r)
    {
        Assert.Contains("default-src 'none'", Assert.Single(r.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(r.Headers.GetValues("Referrer-Policy")));
    }

    [Theory]
    [InlineData("/", HttpStatusCode.Redirect)]
    [InlineData("/dashboard", HttpStatusCode.Redirect)]
    [InlineData("/dashboard/nope.js", HttpStatusCode.NotFound)]
    [InlineData("/dashboard/a/b", HttpStatusCode.NotFound)]
    public async Task RedirectsAndNotFound_CarrySecurityHeaders(string path, HttpStatusCode expected)
    {
        using var client = _app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"http://localhost:{_app.Port()}"),
        });

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        AssertSecurityHeaders(response);
        if (expected == HttpStatusCode.Redirect) Assert.Equal("/dashboard/", response.Headers.Location!.OriginalString);
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
    public async Task Overview_IsServedFromCachePerView_AndRebuiltForANewView()
    {
        _app.Tracker.View = new TrackerView(T0, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, []);
        var ct = TestContext.Current.CancellationToken;

        var first = await _http.GetStringAsync("/api/overview", ct);
        var second = await _http.GetStringAsync("/api/overview", ct);
        _app.Tracker.View = new TrackerView(T0.AddSeconds(2), default, FrozenDictionary<ProcessKey, ProcessView>.Empty, []);
        var third = await _http.GetStringAsync("/api/overview", ct);

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Contains("2026-11-20T12:00:02", third);
    }

    private const string UnitB = "host:local_b";
    private const int CacheLimit = Service.Api.OverviewCache.MaxEntries;

    private void SetView(DateTime ts, params ProcessView[] procs) =>
        _app.Tracker.View = new TrackerView(ts, default, procs.ToFrozenDictionary(p => p.Record.Key), [Session(UnitA), Session(UnitB)]);

    private Task<OverviewDto?> Overview(string query = "") =>
        _http.GetFromJsonAsync<OverviewDto>("/api/overview" + query, ContractJson.Options, TestContext.Current.CancellationToken);

    private static string Expand(params string[] ids) => "?expand=" + Uri.EscapeDataString(string.Join(',', ids));

    [Fact]
    public async Task Overview_HasLoadSessionSummariesFlatRowsForExpandedOnlyAndOrphans()
    {
        SetView(T0,
            Proc(10, "claude.exe", UnitA),
            Proc(11, "node.exe", UnitA, parentPid: 10),
            Proc(12, "Dymola.exe", UnitA, state: ProcessState.Orphan),
            Proc(20, "claude.exe", UnitB));

        var o = await Overview(Expand(UnitA));

        Assert.NotNull(o);
        Assert.Equal(T0, o.TimestampUtc);
        Assert.Equal(CollectorWorker.Interval.TotalSeconds, o.SampleIntervalSeconds);
        Assert.NotNull(o.Load);
        Assert.Equal([UnitA, UnitB], o.Sessions.Select(s => s.Session.UnitId).Order()); // every session has a summary
        // flat rows of the expanded session only, with parent keys; the page builds the tree
        Assert.Equal([Key(10).ToString(), Key(11).ToString(), Key(12).ToString()], o.Processes.Select(p => p.Key));
        Assert.All(o.Processes, p => Assert.Equal(UnitA, p.UnitId));
        Assert.Equal(Key(10).ToString(), o.Processes.Single(p => p.Pid == 11).ParentKey);
        Assert.Null(o.Processes.Single(p => p.Pid == 10).ParentKey);
        Assert.Equal(Key(12).ToString(), Assert.Single(o.Orphans).Key);
    }

    [Fact]
    public async Task Overview_WithoutExpand_HasNoProcessRowsButStillOrphans()
    {
        SetView(T0, Proc(10, "claude.exe", UnitA), Proc(12, "Dymola.exe", UnitA, state: ProcessState.Orphan));

        var o = await Overview();

        Assert.NotNull(o);
        Assert.Empty(o.Processes);
        Assert.Single(o.Orphans);
        Assert.Equal(2, o.Sessions.Count);
    }

    [Fact]
    public async Task Overview_UnknownAndDuplicateExpandIdsAreIgnored()
    {
        SetView(T0, Proc(10, "claude.exe", UnitA), Proc(20, "claude.exe", UnitB));

        var o = await Overview(Expand("nope", UnitB, "", UnitB, " x "));

        Assert.NotNull(o);
        Assert.Equal(Key(20).ToString(), Assert.Single(o.Processes).Key);
    }

    [Fact]
    public void ParseExpand_NormalizesCapsAndFiltersToKnownSessions()
    {
        var view = new TrackerView(T0, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, [Session(UnitA), Session(UnitB)]);

        Assert.Empty(Service.Api.RestEndpoints.ParseExpand(null, view));
        Assert.Empty(Service.Api.RestEndpoints.ParseExpand("  ", view));
        Assert.Equal([UnitA, UnitB], Service.Api.RestEndpoints.ParseExpand($" {UnitB} , {UnitA},{UnitB},zzz", view));
        // ids beyond the 8 KB prefix are never read
        Assert.Equal([UnitA], Service.Api.RestEndpoints.ParseExpand(UnitA + "," + new string('x', 9000) + "," + UnitB, view));
    }

    [Fact]
    public async Task Overview_CachePerExpandSet_IsConsistentAndBounded()
    {
        SetView(T0, Proc(10, "claude.exe", UnitA), Proc(20, "claude.exe", UnitB));
        var ct = TestContext.Current.CancellationToken;

        var a1 = await _http.GetStringAsync("/api/overview" + Expand(UnitA), ct);
        var b = await _http.GetStringAsync("/api/overview" + Expand(UnitB), ct);
        var a2 = await _http.GetStringAsync("/api/overview" + Expand(UnitA), ct);
        for (var i = 0; i < CacheLimit + 2; i++) // churn through the bound: results must stay correct
            await _http.GetStringAsync("/api/overview" + Expand(i % 2 == 0 ? UnitA : UnitB, "u" + i), ct);
        var a3 = await _http.GetStringAsync("/api/overview" + Expand(UnitA), ct);

        Assert.Equal(a1, a2);
        Assert.Equal(a1, a3);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public async Task DeepProcessChain_BeyondDefaultJsonDepth_StillSerializes()
    {
        var chain = new List<ProcessView> { Proc(1, "root.exe", UnitA) };
        for (var pid = 2; pid <= 120; pid++) chain.Add(Proc(pid, "child.exe", UnitA, parentPid: pid - 1));
        SetView(T0, [.. chain]);

        var o = await Overview(Expand(UnitA));
        var tree = await _http.GetFromJsonAsync<SessionTreeDto>($"/api/sessions/{UnitA}", ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(o);
        Assert.Equal(120, o.Processes.Count);
        Assert.NotNull(tree);
        var depth = 0;
        for (var n = tree.Processes.Single(); ; n = n.Children.Single())
        {
            depth++;
            if (n.Children.Count == 0) break;
        }
        Assert.Equal(120, depth);
    }
}
