using System.Text.Json;
using ProcessTracker.Core.Hooks;
using ProcessTracker.Core.Load;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Core.Updates;

namespace ProcessTracker.Service.Api;

/// <summary>
/// Endpoints for the Claude Code plugin's hooks under <c>/api/hooks</c>. The body is the hook's own JSON input (unknown
/// fields ignored; parsed whatever the Content-Type, since curl posts it as a form by default). A body that is not
/// JSON gets 400, which the hook treats as a silent, non-blocking failure. Responses only ever add context: none
/// carries a permission decision, so a hook never blocks a tool call.
/// </summary>
public static class HookEndpoints
{
    public static IEndpointRouteBuilder MapHookEndpoints(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/hooks");
        hooks.MapPost("/pre-tool-use", PreToolUse);
        hooks.MapPost("/session-start", SessionStart);
        return app;
    }

    /// <summary>
    /// Runs before every Bash, PowerShell, Task and Agent call, so it answers fast: <c>{}</c> unless the call is heavy
    /// work and the machine is at Moderate or above. The full High/Critical warning is rationed per <c>session_id</c>.
    /// </summary>
    private static async Task<IResult> PreToolUse(
        HttpContext ctx, ITrackerHost host, HookWarningLimiter limiter, IUserActivity activity, ServiceOptions options)
    {
        if (await ReadJson(ctx) is not { } input) return Results.BadRequest("the body must be JSON");

        var sessionId = Str(input, "session_id");
        var toolName = Str(input, "tool_name");
        var command = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("tool_input", out var toolInput)
            ? Str(toolInput, "command")
            : null;
        if (toolName is null || !HeavyCommandClassifier.IsHeavy(toolName, command, options.HeavyCommandRegexes()))
            return Nothing;

        var caller = FindSession(host.View, sessionId);
        var load = RestEndpoints.Load(host, caller?.Info.UnitId);
        if (load.Pressure == Pressure.Low) return Nothing;

        var full = load.Pressure >= Pressure.High && limiter.TakeFull(sessionId ?? "");
        var text = HookAdvisor.PreToolUse(load, heavy: true, full, activity.SinceLastInput(), caller?.Info.DisplayTitle);
        return text is null ? Nothing : Context("PreToolUse", text);
    }

    /// <summary>The context a session starts with; the plugin's script adds <c>plugin_version</c> to the hook input.</summary>
    private static async Task<IResult> SessionStart(
        HttpContext ctx, ITrackerHost host, IUserActivity activity, ServiceOptions options)
    {
        if (await ReadJson(ctx) is not { } input) return Results.BadRequest("the body must be JSON");

        var caller = FindSession(host.View, Str(input, "session_id"));
        var load = RestEndpoints.Load(host, caller?.Info.UnitId);
        // An unparseable tracker version (never in a release build) leaves out the version-skew line.
        var trackerVersion = ReleaseVersion.Numeric(RestEndpoints.Version);
        var pluginVersion = trackerVersion is not null && Str(input, "plugin_version") is { } p ? ReleaseVersion.Numeric(p) : null;
        var text = HookAdvisor.SessionStart(load, options.Mode.WireName(), activity.SinceLastInput(), pluginVersion,
            trackerVersion ?? new Version(0, 0, 0));
        return Context("SessionStart", text);
    }

    private static readonly IResult Nothing = Results.Text("{}", "application/json");

    private static IResult Context(string hookEventName, string additionalContext) =>
        Results.Json(new { hookSpecificOutput = new { hookEventName, additionalContext } });

    /// <summary>The session that <paramref name="claudeSessionId"/> belongs to, a live one first; null when unknown.</summary>
    private static SessionView? FindSession(TrackerView view, string? claudeSessionId) =>
        string.IsNullOrEmpty(claudeSessionId)
            ? null
            : view.Sessions
                .Where(s => s.Info.ClaudeSessionIds.Contains(claudeSessionId, StringComparer.Ordinal))
                .OrderByDescending(s => s.IsLive)
                .FirstOrDefault();

    /// <summary>The body as JSON, or null when it is not JSON (including an empty body).</summary>
    private static async Task<JsonElement?> ReadJson(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
