using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class OwnerGateTests
{
    private sealed class FixedGate(bool owner) : IRequestOwnerGate
    {
        public bool IsOwner(HttpContext ctx) => owner;
    }

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly KillAuditDto Kill = new("1234@5678", "MATLAB.exe", null, "proctrack.exe", Tree: true, Result: "killed");

    // Until the hook endpoint exists (Task 7), POST /api/audit/kill stands in for it: a POST with a body whose handler
    // is observable through FakeTrackerHost.Kills, so no recorded kill proves the request never reached the handler.
    [Fact]
    public async Task OwnerGate_OtherUser_Gets403AndHandlerNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(false) };
        using var http = app.CreateLocalClient();

        var load = await http.GetAsync("/api/load", ct);
        var audit = await http.PostAsJsonAsync("/api/audit/kill", Kill, ContractJson.Options, ct);

        Assert.Equal(HttpStatusCode.Forbidden, load.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        Assert.Equal(OwnerGate.ForbiddenMessage, await audit.Content.ReadAsStringAsync(ct));
        Assert.Empty(app.Tracker.Kills);
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
        await using var app = new TestApp(Now, RunMode.User) { OwnerGate = new FixedGate(true) };
        using var http = app.CreateLocalClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/load", ct)).StatusCode);
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
