using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Load;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Storage;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Service.Api;

/// <summary>The REST API under <c>/api</c>. Every handler reads the immutable <see cref="TrackerView"/> of the latest tick.</summary>
public static class RestEndpoints
{
    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromMinutes(30);
    private const int LoadFrames = 5;

    /// <summary>
    /// InformationalVersion of the service assembly, as reported by <c>/api/health</c>; the literal "unhealthy" in a
    /// SIMULATE_UNHEALTHY build, which exists only to test the updater's rollback.
    /// </summary>
    public static string Version { get; } =
#if SIMULATE_UNHEALTHY
        "unhealthy";
#else
        typeof(RestEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
#endif

    public static IEndpointRouteBuilder MapRestEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (ITrackerHost host, TimeProvider time) => Health(host, time));

        api.MapGet("/load", (ITrackerHost host, ICallerResolver callers, HttpContext ctx) => Load(host, CallerUnit(callers, ctx)));

        api.MapGet("/sessions", (ITrackerHost host) => Sessions(host.View));

        api.MapGet("/sessions/{unitId}", Results<Ok<SessionTreeDto>, NotFound> (string unitId, ITrackerHost host) =>
            SessionTree(host.View, unitId) is { } tree ? TypedResults.Ok(tree) : TypedResults.NotFound());

        api.MapGet("/orphans", (ITrackerHost host) => Orphans(host.View));

        api.MapGet("/overview", (ITrackerHost host, OverviewCache cache) =>
            Results.Bytes(cache.GetJson(host), "application/json; charset=utf-8"));

        api.MapGet("/processes/by-pid/{pid:int}", (int pid, ITrackerHost host) =>
            host.View.Processes.Values.Where(p => p.Record.Key.Pid == pid).Select(ProcessViewDto.From).ToArray());

        api.MapGet("/processes/{key}", Results<Ok<ProcessViewDto>, NotFound, BadRequest<string>> (string key, ITrackerHost host) =>
            !ProcessKey.TryParse(key, out var k) ? TypedResults.BadRequest("key must be pid@ticks")
            : host.View.Processes.TryGetValue(k, out var p) ? TypedResults.Ok(ProcessViewDto.From(p))
            : TypedResults.NotFound());

        api.MapGet("/history", (string? unit, string? key, string? from, string? to,
                ITrackerHost host, ServiceOptions options, TimeProvider time) =>
            History(host, options.Retention, time.GetUtcNow().UtcDateTime, unit, key, from, to));

        // Written by hand so that an unknown caller is the JSON literal null rather than an empty body.
        api.MapGet("/caller", (HttpContext ctx, ICallerResolver callers, IOptions<JsonOptions> json) =>
            ctx.Response.WriteAsJsonAsync(
                callers.Resolve(ctx) is { } a ? AttributionDto.From(a) : null, json.Value.SerializerOptions, ctx.RequestAborted));

        api.MapPost("/audit/kill", Results<NoContent, BadRequest<string>> (KillAuditDto dto, ITrackerHost host, TimeProvider time) =>
        {
            if (!ProcessKey.TryParse(dto.Key, out var key)) return TypedResults.BadRequest("key must be pid@ticks");
            if (string.IsNullOrWhiteSpace(dto.ImageName) || string.IsNullOrWhiteSpace(dto.CallerImage)
                || string.IsNullOrWhiteSpace(dto.Result))
                return TypedResults.BadRequest("imageName, callerImage and result are required");

            var ts = dto.TsUtc is { } t ? ContractJson.AsUtc(t) : time.GetUtcNow().UtcDateTime;
            host.RecordKill(new KillAudit(ts, key, dto.ImageName, dto.UnitId, dto.CallerImage, dto.Tree, dto.Result));
            return TypedResults.NoContent();
        });

        return app;
    }

    public static HealthDto Health(ITrackerHost host, TimeProvider time)
    {
        var view = host.View;
        DateTime? last = view.TimestampUtc == TrackerView.Empty.TimestampUtc ? null : ContractJson.AsUtc(view.TimestampUtc);
        double? age = last is { } l ? Math.Max(0, (time.GetUtcNow().UtcDateTime - l).TotalSeconds) : null;
        return new HealthDto(Version, ContractJson.AsUtc(host.StartedUtc), last, age);
    }

    /// <summary>Machine pressure over the last few ticks, with the caller's own usage when <paramref name="callerUnit"/> is in the view.</summary>
    public static LoadReport Load(ITrackerHost host, string? callerUnit) =>
        LoadAssessor.Assess(host.View, host.Recent(LoadFrames), callerUnit, new LoadOptions());

    public static SessionViewDto[] Sessions(TrackerView view) => view.Sessions.Select(SessionViewDto.From).ToArray();

    /// <summary>One consistent snapshot for the dashboard: load, every session with its tree, and the orphans, all from one view.</summary>
    public static OverviewDto Overview(ITrackerHost host) => Overview(host, host.View);

    /// <summary>The overview of <paramref name="view"/> (which must be <c>host.View</c> as read by the caller).</summary>
    public static OverviewDto Overview(ITrackerHost host, TrackerView view)
    {
        var load = LoadAssessor.Assess(view, host.Recent(LoadFrames), null, new LoadOptions());
        var processes = view.Processes.Values.ToList();
        var sessions = view.Sessions.Select(s => SessionTreeDto.Build(s, processes)).ToArray();
        return new OverviewDto(load, sessions, Orphans(view), ContractJson.AsUtc(view.TimestampUtc),
            CollectorWorker.Interval.TotalSeconds);
    }

    /// <summary>The caller's session unit, or null when the caller is not a session.</summary>
    public static string? CallerUnit(ICallerResolver callers, HttpContext ctx) =>
        callers.Resolve(ctx) is { Kind: AttributionKind.Session, UnitId: { } unit } ? unit : null;

    /// <summary>The session with its process tree, or null if the unit is not in the view.</summary>
    public static SessionTreeDto? SessionTree(TrackerView view, string unitId) =>
        view.Sessions.FirstOrDefault(s => s.Info.UnitId == unitId) is { } session
            ? SessionTreeDto.Build(session, view.Processes.Values)
            : null;

    /// <summary>Orphaned processes: alertable first, then by private bytes, largest first.</summary>
    public static ProcessViewDto[] Orphans(TrackerView view) =>
        view.Processes.Values
            .Where(p => p.State == ProcessState.Orphan)
            .OrderByDescending(p => p.Alertable)
            .ThenByDescending(p => p.Latest.PrivateBytes)
            .ThenBy(p => p.Record.Key.Pid)
            .Select(ProcessViewDto.From)
            .ToArray();

    /// <summary>
    /// A session (<paramref name="unit"/>) or process (<paramref name="key"/>) series. The range defaults to the
    /// 30 minutes before <paramref name="to"/> (default now); both ends are clamped to [now - retention, now].
    /// </summary>
    public static IResult History(
        ITrackerHost host, TimeSpan retention, DateTime now, string? unit, string? key, string? from, string? to)
    {
        if ((unit is null) == (key is null)) return TypedResults.BadRequest("specify exactly one of unit or key");
        ProcessKey processKey = default;
        if (key is not null && !ProcessKey.TryParse(key, out processKey)) return TypedResults.BadRequest("key must be pid@ticks");
        if (!TryParseUtc(to, out var toArg) || !TryParseUtc(from, out var fromArg))
            return TypedResults.BadRequest("from and to must be ISO-8601 timestamps");
        var toUtc = toArg ?? now;
        var fromUtc = fromArg ?? (toUtc.Ticks > DefaultHistoryWindow.Ticks ? toUtc - DefaultHistoryWindow : MinUtc);
        if (fromUtc > toUtc) return TypedResults.BadRequest("from must not be after to");

        var series = unit is not null
            ? SessionHistory(host, retention, now, unit, fromUtc, toUtc)
            : ProcessHistory(host, retention, now, processKey, fromUtc, toUtc);
        return series is null ? TypedResults.NotFound() : TypedResults.Ok(series);
    }

    /// <summary>
    /// A session unit's series over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] clamped into the retention
    /// window, or null if the unit is not in the view.
    /// </summary>
    public static HistorySeriesDto? SessionHistory(
        ITrackerHost host, TimeSpan retention, DateTime now, string unit, DateTime fromUtc, DateTime toUtc)
    {
        ClampToRetention(retention, now, ref fromUtc, ref toUtc);
        if (host.View.Sessions.FirstOrDefault(s => s.Info.UnitId == unit) is not { } session) return null;
        var points = host.History(fromUtc, toUtc)
            .OfType<SessionRec>()
            .Select(r => r.Value)
            .Where(a => a.UnitId == unit)
            .Select(a => new HistoryPointDto(
                ContractJson.AsUtc(a.TimestampUtc), a.CpuCores, a.PrivateBytes, a.ReadBytesDelta, a.WriteBytesDelta, a.ProcessCount))
            .OrderBy(p => p.TimestampUtc)
            .ToList();
        return new HistorySeriesDto(unit, null, session.Info.Title, fromUtc, toUtc, points);
    }

    /// <summary>
    /// A process's series over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] clamped into the retention
    /// window, or null if the process is neither live nor in that range of history.
    /// </summary>
    public static HistorySeriesDto? ProcessHistory(
        ITrackerHost host, TimeSpan retention, DateTime now, ProcessKey processKey, DateTime fromUtc, DateTime toUtc)
    {
        ClampToRetention(retention, now, ref fromUtc, ref toUtc);

        // Process metadata comes from the live view; for an exited process, from its info record if it is in range.
        var name = host.View.Processes.TryGetValue(processKey, out var live) ? live.Record.ImageName : null;
        var processPoints = new List<HistoryPointDto>();
        foreach (var r in host.History(fromUtc, toUtc))
        {
            if (r is ProcessRec { Value: var s } && s.Key == processKey)
            {
                processPoints.Add(new HistoryPointDto(
                    ContractJson.AsUtc(s.TimestampUtc), s.CpuCores, s.PrivateBytes, s.ReadBytesDelta, s.WriteBytesDelta, null));
            }
            else if (r is ProcessInfoRec { Value: var info } && info.Key == processKey)
            {
                name ??= info.ImageName;
            }
        }
        if (live is null && name is null && processPoints.Count == 0) return null;
        processPoints.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return new HistorySeriesDto(null, processKey.ToString(), name, fromUtc, toUtc, processPoints);
    }

    /// <summary>Clamps both ends into [now - retention, now]; a range entirely outside it becomes an empty instant.</summary>
    private static void ClampToRetention(TimeSpan retention, DateTime now, ref DateTime fromUtc, ref DateTime toUtc)
    {
        var floor = now - retention;
        fromUtc = Clamp(fromUtc, floor, now);
        toUtc = Clamp(toUtc, floor, now);
    }

    private static readonly DateTime MinUtc = new(0, DateTimeKind.Utc);

    private static DateTime Clamp(DateTime t, DateTime min, DateTime max) => t < min ? min : t > max ? max : t;

    private static bool TryParseUtc(string? s, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        utc = parsed;
        return true;
    }
}
