using System.Collections.Frozen;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Load;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class McpToolsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1L << 30;

    private readonly TestApp _app = new(T0);

    public void Dispose() => _app.Dispose();

    /// <summary>An MCP client over the in-memory test server, optionally sending an X-Claude-Session header.</summary>
    private async Task<McpClient> Connect(string? sessionHeader = null)
    {
        var http = _app.CreateLocalClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (sessionHeader is not null) options.AdditionalHeaders = new Dictionary<string, string> { ["X-Claude-Session"] = sessionHeader };
        var transport = new HttpClientTransport(options, http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static T Json<T>(CallToolResult result) => JsonSerializer.Deserialize<T>(Text(result), ContractJson.Options)!;

    private static SessionView Session(string unit) =>
        new(new SessionInfo(unit, null, [], null, T0, null, @"C:\work", "a title", null), true, 1f, 100, 1);

    [Fact]
    public async Task Mcp_ListsFiveTools_AllReadOnly()
    {
        await using var client = await Connect();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["get_machine_load", "get_my_session", "get_process_history", "list_orphans", "list_sessions"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t =>
        {
            Assert.DoesNotContain("kill", t.Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
        });
    }

    [Fact]
    public async Task Mcp_Initialize_SendsLoadCheckInstructions()
    {
        await using var client = await Connect();

        var instructions = client.ServerInstructions;

        Assert.NotNull(instructions);
        Assert.Contains("get_machine_load", instructions);
        Assert.Contains("suggestedParallelism", instructions);
        Assert.Contains("Critical", instructions);
        Assert.Equal(Mcp.ProcessTrackerTools.ServerInstructions, instructions);
    }

    [Fact]
    public async Task Mcp_GetMachineLoad_ReturnsPressure()
    {
        // 1.5 GB commit headroom is under the 2 GB critical floor.
        var system = new SystemSample(T0, 10, 1.6f, 16, 62 * Gb + Gb / 2, 64 * Gb, 8 * Gb, 32 * Gb);
        _app.Tracker.View = new TrackerView(T0, system, FrozenDictionary<ProcessKey, ProcessView>.Empty, []);
        await using var client = await Connect();

        var result = await client.CallToolAsync("get_machine_load", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var load = Json<LoadReport>(result);
        Assert.Equal(Pressure.Critical, load.Pressure);
        Assert.Equal(1.5, load.CommitHeadroomGb, 6);
        Assert.Contains("\"pressure\":\"Critical\"", Text(result), StringComparison.Ordinal); // same contract as REST
    }

    [Fact]
    public async Task Mcp_GetMySession_NotASession_SaysSo()
    {
        _app.Tracker.View = new TrackerView(T0, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, [Session("host:local_a")]);
        await using var client = await Connect();

        var result = await client.CallToolAsync("get_my_session", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(Mcp.ProcessTrackerTools.NotASession, Text(result)); // plain text, not a JSON string
    }

    [Fact]
    public async Task Mcp_GetMySession_HeaderFallback_ReturnsSessionTree()
    {
        _app.Tracker.View = new TrackerView(T0, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, [Session("host:local_a")]);
        await using var client = await Connect(sessionHeader: "local_a");

        var result = await client.CallToolAsync("get_my_session", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var tree = Json<SessionTreeDto>(result);
        Assert.Equal("host:local_a", tree.Session.UnitId);
        Assert.Equal("a title", tree.Session.Title);
    }

    [Fact]
    public async Task Mcp_ListSessions_IncludesTitleSourceProjectBranchSummary()
    {
        var desktop = new DesktopSessionInfo("Sidebar title", "main", null, null, @"C:\repos\Widget", "Did a thing.", null);
        var s = new SessionView(new SessionInfo("host:local_a", "local_a", [], null, T0, null, @"C:\work", "a title", null, desktop),
            true, 1f, 100, 1);
        _app.Tracker.View = new TrackerView(T0, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, [s]);
        await using var client = await Connect();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("titleSource", tools.Single(t => t.Name == "list_sessions").Description, StringComparison.Ordinal);

        var result = await client.CallToolAsync("list_sessions", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var dto = Assert.Single(Json<SessionViewDto[]>(result));
        Assert.Equal("Sidebar title", dto.Title);
        Assert.Equal("desktop", dto.TitleSource);
        Assert.Equal("Widget", dto.Project);
        Assert.Equal("main", dto.Branch);
        Assert.Equal("Did a thing.", dto.Summary);
    }

    [Fact]
    public async Task Mcp_GetProcessHistory_CoversLastMinutes()
    {
        var key = new ProcessKey(42, 1_000_042);
        var record = new ProcessRecord(key, null, "MATLAB.exe", null, null, Attribution.Unattributed, T0, null);
        var sample = new ProcessSample(key, T0.AddMinutes(-5), 2f, 3 * Gb, 3 * Gb, 0, 0, 0, 0, 0, 0, 0, 10, 2);
        _app.Tracker.View = new TrackerView(T0, default,
            new Dictionary<ProcessKey, ProcessView> { [key] = new(record, sample, default, null, default, false) }.ToFrozenDictionary(), []);
        _app.Tracker.Records.Add(new ProcessRec(sample));
        _app.Tracker.Records.Add(new ProcessRec(sample with { TimestampUtc = T0.AddMinutes(-20) })); // outside 10 min
        await using var client = await Connect();

        var result = await client.CallToolAsync("get_process_history",
            new Dictionary<string, object?> { ["key"] = key.ToString(), ["minutes"] = 10 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var series = Json<HistorySeriesDto>(result);
        Assert.Equal(key.ToString(), series.Key);
        Assert.Equal("MATLAB.exe", series.Name);
        Assert.Equal(T0.AddMinutes(-10), series.FromUtc);
        Assert.Equal(T0, series.ToUtc);
        Assert.Equal(T0.AddMinutes(-5), Assert.Single(series.Points).TimestampUtc);

        var bad = await client.CallToolAsync("get_process_history",
            new Dictionary<string, object?> { ["key"] = "nope" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bad.IsError);
        Assert.Contains("pid@ticks", Text(bad), StringComparison.Ordinal);
    }
}
