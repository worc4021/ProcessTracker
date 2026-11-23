using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Hooks;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class OwnerGateTests
{
    private sealed class FixedGate(bool owner) : IRequestOwnerGate
    {
        public bool IsOwner(HttpContext ctx) => owner;
    }

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class CountingActivity : IUserActivity
    {
        public int Calls;

        public TimeSpan? SinceLastInput()
        {
            Interlocked.Increment(ref Calls);
            return TimeSpan.FromSeconds(1);
        }
    }

    private const string HeavyHook = """{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Task","tool_input":{}}""";

    private const long Gb = 1L << 30;

    /// <summary>
    /// A view at Critical pressure (1.5 GB commit headroom). The hook handler asks IUserActivity for the last input only
    /// for a heavy call at Moderate or above, so only then does the probe count show whether the advisor ran.
    /// </summary>
    private static void AtCritical(TestApp app) =>
        app.Tracker.View = new TrackerView(Now, new SystemSample(Now, 10, 1.6f, 16, 62 * Gb + Gb / 2, 64 * Gb, 8 * Gb, 32 * Gb),
            FrozenDictionary<ProcessKey, ProcessView>.Empty, []);

    private static readonly KillAuditDto Kill = new("1234@5678", "MATLAB.exe", null, "proctrack.exe", Tree: true, Result: "killed");

    // The PreToolUse hook is what another user's sessions would hit on every tool call. With the machine at Critical, a
    // heavy Task call that got past the gate would reach the advisor and ask IUserActivity for the last input (the owner
    // test shows it does), so zero probe calls proves the advisor never ran. The kill audit is observable through
    // FakeTrackerHost.Kills: no recorded kill proves that handler never ran either.
    [Fact]
    public async Task OwnerGate_OtherUser_Gets403AndHandlerNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new CountingActivity();
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(false), UserActivity = activity };
        AtCritical(app);
        using var http = app.CreateLocalClient();

        var load = await http.GetAsync("/api/load", ct);
        var audit = await http.PostAsJsonAsync("/api/audit/kill", Kill, ContractJson.Options, ct);
        var hook = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(HeavyHook, Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.Forbidden, load.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        Assert.Equal(OwnerGate.ForbiddenMessage, await audit.Content.ReadAsStringAsync(ct));
        Assert.Empty(app.Tracker.Kills);
        Assert.Equal(HttpStatusCode.Forbidden, hook.StatusCode);
        Assert.Equal(OwnerGate.ForbiddenMessage, await hook.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, activity.Calls);
    }

    [Fact]
    public async Task OwnerGate_OtherUser_HealthStillReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(false) };
        using var http = app.CreateLocalClient();

        var response = await http.GetAsync("/api/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"mode\":\"user\"", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task OwnerGate_OtherUser_McpForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(false) };
        using var http = app.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OwnerGate_Owner_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = new CountingActivity();
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(true), UserActivity = activity };
        AtCritical(app);
        using var http = app.CreateLocalClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/load", ct)).StatusCode);
        var hook = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(HeavyHook, Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.Contains("additionalContext", await hook.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(1, activity.Calls);
        var audit = await http.PostAsJsonAsync("/api/audit/kill", Kill, ContractJson.Options, ct);

        Assert.Equal(HttpStatusCode.NoContent, audit.StatusCode);
        Assert.Single(app.Tracker.Kills);
    }

    [Fact]
    public async Task OwnerGate_ServiceMode_NotApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new TestApp(Now, RunMode.Service) { OwnerGate = new FixedGate(false) };
        using var http = app.CreateLocalClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/load", ct)).StatusCode);
    }
}
