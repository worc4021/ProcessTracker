using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using ProcessTracker.Core.Hooks;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class HookEndpointsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1L << 30;
    private const string ClaudeSession = "5f0c1d2e-0000-4000-8000-000000000001";

    private sealed class FixedActivity(TimeSpan? since) : IUserActivity
    {
        public int Calls;

        public TimeSpan? SinceLastInput()
        {
            Interlocked.Increment(ref Calls);
            return since;
        }
    }

    private readonly TestApp _app = new(T0) { UserActivity = new FixedActivity(TimeSpan.FromSeconds(4)) };
    private readonly HttpClient _http;

    public HookEndpointsTests() => _http = _app.CreateLocalClient();

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    /// <summary>A view at Critical (1.5 GB commit headroom) with the caller's session and a bigger one.</summary>
    private void AtCritical()
    {
        var system = new SystemSample(T0, 10, 1.6f, 16, 62 * Gb + Gb / 2, 64 * Gb, 8 * Gb, 32 * Gb);
        SessionView[] sessions =
        [
            new(new SessionInfo("host:me", null, [ClaudeSession], null, T0, null, @"C:\me", "My session", null), true, 1f, 2 * Gb, 4),
            new(new SessionInfo("host:big", null, ["other"], null, T0, null, @"C:\big", "Big build", null), true, 8f, 20 * Gb, 60),
        ];
        _app.Tracker.View = new TrackerView(T0, system, FrozenDictionary<ProcessKey, ProcessView>.Empty, sessions);
    }

    private async Task<HttpResponseMessage> Post(string path, string body) =>
        await _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static string PreToolUseBody(string tool = "Bash", string command = "dotnet build ProcessTracker.slnx") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["session_id"] = ClaudeSession,
            ["transcript_path"] = @"C:\Users\x\.claude\projects\p\s.jsonl",
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = new Dictionary<string, object> { ["command"] = command, ["description"] = "build" },
        });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
    }

    [Fact]
    public async Task PreToolUse_HeavyAtCritical_ReturnsAdditionalContextNoPermissionDecision()
    {
        AtCritical();

        var response = await Post("/api/hooks/pre-tool-use", PreToolUseBody());
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var output = (await Json(response)).GetProperty("hookSpecificOutput");

        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        var context = output.GetProperty("additionalContext").GetString()!;
        Assert.StartsWith("ProcessTracker: machine at Critical", context, StringComparison.Ordinal);
        Assert.Contains("- My session: 1.0 cores, 2.0 GB (this session)", context, StringComparison.Ordinal);
        Assert.Contains("User last active 4 s ago.", context, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreToolUse_SecondCallWithinAMinute_ShortLine()
    {
        AtCritical();
        await Post("/api/hooks/pre-tool-use", PreToolUseBody());

        var output = (await Json(await Post("/api/hooks/pre-tool-use", PreToolUseBody("Task", "")))).GetProperty("hookSpecificOutput");

        Assert.Equal("ProcessTracker: machine still at Critical; suggested parallelism 1.",
            output.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public async Task PreToolUse_TrivialCommand_ReturnsEmptyObject()
    {
        AtCritical();

        var response = await Post("/api/hooks/pre-tool-use", PreToolUseBody(command: "git status"));

        Assert.Equal("{}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"tool_name":"Bash","tool_input":"dotnet build"}""")]
    [InlineData("[]")]
    public async Task PreToolUse_UnknownPayload_ReturnsEmptyObject(string body)
    {
        AtCritical();

        var response = await Post("/api/hooks/pre-tool-use", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task PreToolUse_MalformedJson_Returns400(string body)
    {
        var response = await Post("/api/hooks/pre-tool-use", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreToolUse_FormContentType_StillParsed()
    {
        // curl -d sends application/x-www-form-urlencoded unless told otherwise; the body is still the hook's JSON.
        AtCritical();

        var response = await _http.PostAsync("/api/hooks/pre-tool-use",
            new StringContent(PreToolUseBody(), Encoding.UTF8, "application/x-www-form-urlencoded"),
            TestContext.Current.CancellationToken);

        Assert.True((await Json(response)).TryGetProperty("hookSpecificOutput", out _));
    }

    [Fact]
    public async Task SessionStart_ReturnsContext()
    {
        AtCritical();
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["session_id"] = ClaudeSession,
            ["hook_event_name"] = "SessionStart",
            ["source"] = "startup",
            ["plugin_version"] = "0.0.1",
        });

        var raw = await (await Post("/api/hooks/session-start", body)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var output = JsonDocument.Parse(raw).RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("SessionStart", output.GetProperty("hookEventName").GetString());
        var context = output.GetProperty("additionalContext").GetString()!;
        Assert.StartsWith("ProcessTracker (dev mode): machine at Critical, suggested parallelism 1.", context, StringComparison.Ordinal);
        Assert.Contains("Before heavy local work, check get_machine_load", context, StringComparison.Ordinal);
        Assert.Contains("The process-tracker plugin (0.0.1) is older than the tracker", context, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionStart_MalformedJson_Returns400()
    {
        var response = await Post("/api/hooks/session-start", "{");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
