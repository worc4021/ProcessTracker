using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Tracking;

namespace ProcessTracker.Core.Contracts;

// REST/MCP contracts shared by the service and its clients. Process keys are rendered as "pid@ticks";
// every timestamp is UTC and serializes as ISO-8601 with a trailing "Z". Command lines are deliberately
// not exposed: they can hold secrets and the API is reachable by every local user.

/// <summary>JSON settings every endpoint and client uses: web defaults (camelCase) with enums as strings.</summary>
public static class ContractJson
{
    /// <remarks>The resolver is set explicitly so that the instance can be frozen, which the MCP SDK does.</remarks>
    public static JsonSerializerOptions Options { get; } =
        Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    /// <summary>Applies the contract settings on top of web defaults (for ASP.NET Core's own options instance).</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new JsonStringEnumConverter());
        o.MaxDepth = 256; // a process tree can nest deeper than the default 64

        return o;
    }

    /// <summary>Treats an unspecified kind as UTC (every time in this system is UTC).</summary>
    public static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    public static DateTime? AsUtc(DateTime? d) => d is { } v ? AsUtc(v) : null;
}

public sealed record AttributionDto(AttributionKind Kind, string? UnitId, string? ClaudeSessionId, bool IsAnchor)
{
    public static AttributionDto From(Model.Attribution a) => new(a.Kind, a.UnitId, a.ClaudeSessionId, a.IsAnchor);
}

/// <summary>One live process as of the latest tick. Byte deltas cover the last sampling interval.</summary>
public sealed record ProcessViewDto(
    string Key,
    int Pid,
    string? ParentKey,
    string ImageName,
    string? ImagePath,
    AttributionDto Attribution,
    DateTime FirstSeenUtc,
    DateTime SampledUtc,
    float CpuCores,
    long PrivateBytes,
    long WorkingSet,
    long ReadBytesDelta,
    long WriteBytesDelta,
    long OtherBytesDelta,
    int HandleCount,
    int ThreadCount,
    DateTime? LastActiveUtc,
    DateTime? EffectiveLastActiveUtc,
    ProcessState State,
    bool Alertable)
{
    public static ProcessViewDto From(ProcessView v)
    {
        var r = v.Record;
        var s = v.Latest;
        return new ProcessViewDto(
            r.Key.ToString(), r.Key.Pid, r.Parent?.ToString(), r.ImageName, r.ImagePath,
            AttributionDto.From(r.Attribution), ContractJson.AsUtc(r.FirstSeenUtc), ContractJson.AsUtc(s.TimestampUtc),
            s.CpuCores, s.PrivateBytes, s.WorkingSet, s.ReadBytesDelta, s.WriteBytesDelta, s.OtherBytesDelta,
            s.HandleCount, s.ThreadCount,
            ContractJson.AsUtc(v.Activity.LastActiveAt), ContractJson.AsUtc(v.EffectiveLastActive), v.State, v.Alertable);
    }
}

/// <summary>
/// A session unit with the totals of its live processes in the latest tick. <see cref="Title"/> is the best known
/// name (the desktop app's sidebar title, else the first prompt, else the project folder, else the unit id) and
/// <see cref="TitleSource"/> says which ("desktop", "transcript", "folder", "id"). <see cref="Project"/> is the leaf
/// folder of the session's origin folder (the repository of a worktree session) or cwd; <see cref="Branch"/> and
/// <see cref="Summary"/> (of the last turn) come from the desktop app and are null for terminal sessions.
/// </summary>
public sealed record SessionViewDto(
    string UnitId,
    string? HostSessionId,
    IReadOnlyList<string> ClaudeSessionIds,
    string? AnchorKey,
    DateTime FirstSeenUtc,
    DateTime? EndedUtc,
    string? Cwd,
    string? Title,
    DateTime? LastTranscriptWriteUtc,
    bool IsLive,
    float CpuCores,
    long PrivateBytes,
    int ProcessCount,
    string? TitleSource = null,
    string? Project = null,
    string? Branch = null,
    string? Summary = null)
{
    public static SessionViewDto From(SessionView v)
    {
        var i = v.Info;
        return new SessionViewDto(
            i.UnitId, i.HostSessionId, i.ClaudeSessionIds, i.Anchor?.ToString(),
            ContractJson.AsUtc(i.FirstSeenUtc), ContractJson.AsUtc(i.EndedUtc), i.EffectiveCwd, i.DisplayTitle,
            ContractJson.AsUtc(i.LastTranscriptWriteUtc), v.IsLive, v.CpuCores, v.PrivateBytes, v.ProcessCount,
            i.TitleSource, i.Project, i.Branch, i.Summary);
    }
}

/// <summary>A process and its child processes within the same session.</summary>
public sealed record ProcessTreeNodeDto(ProcessViewDto Process, IReadOnlyList<ProcessTreeNodeDto> Children);

/// <summary>A session with its live processes as a forest (a process whose parent is not in the session is a root).</summary>
public sealed record SessionTreeDto(SessionViewDto Session, IReadOnlyList<ProcessTreeNodeDto> Processes)
{
    public static SessionTreeDto Build(SessionView session, IEnumerable<ProcessView> processes)
    {
        var members = processes
            .Where(p => p.Record.Attribution.UnitId == session.Info.UnitId)
            .OrderBy(p => p.Record.Key.CreateTimeTicks)
            .ThenBy(p => p.Record.Key.Pid)
            .ToList();
        var keys = members.Select(p => p.Record.Key).ToHashSet();
        var children = members
            .Where(p => p.Record.Parent is { } parent && keys.Contains(parent))
            .ToLookup(p => p.Record.Parent!.Value);

        ProcessTreeNodeDto Node(ProcessView p) =>
            new(ProcessViewDto.From(p), children[p.Record.Key].Select(Node).ToList());

        var roots = members.Where(p => p.Record.Parent is not { } parent || !keys.Contains(parent)).Select(Node).ToList();
        return new SessionTreeDto(SessionViewDto.From(session), roots);
    }
}

/// <summary>
/// The fields of a process that the dashboard renders. A process whose <see cref="ParentKey"/> is not in the same
/// session's rows is a root; the page builds the tree from the flat list.
/// </summary>
public sealed record OverviewProcessDto(
    string Key,
    string? ParentKey,
    string? UnitId,
    int Pid,
    string ImageName,
    float CpuCores,
    long PrivateBytes,
    long WorkingSet,
    long ReadBytesDelta,
    long WriteBytesDelta,
    DateTime? EffectiveLastActiveUtc,
    ProcessState State,
    bool Alertable)
{
    public static OverviewProcessDto From(ProcessView v)
    {
        var r = v.Record;
        var s = v.Latest;
        return new OverviewProcessDto(
            r.Key.ToString(), r.Parent?.ToString(), r.Attribution.UnitId, r.Key.Pid, r.ImageName,
            s.CpuCores, s.PrivateBytes, s.WorkingSet, s.ReadBytesDelta, s.WriteBytesDelta,
            ContractJson.AsUtc(v.EffectiveLastActive), v.State, v.Alertable);
    }
}

/// <summary>A session summary; LastActiveUtc is the most recent activity of any of its live processes.</summary>
public sealed record OverviewSessionDto(SessionViewDto Session, DateTime? LastActiveUtc);

/// <summary>
/// Everything the dashboard shows, built from one <see cref="TrackerView"/> so the page is consistent: machine load,
/// a summary of every session, the process rows (flat, one list) of only the sessions the caller asked to expand, and
/// the orphans.
/// </summary>
public sealed record OverviewDto(
    Load.LoadReport Load, IReadOnlyList<OverviewSessionDto> Sessions, IReadOnlyList<OverviewProcessDto> Processes,
    IReadOnlyList<OverviewProcessDto> Orphans, DateTime TimestampUtc, double SampleIntervalSeconds);

/// <summary>One history point. ProcessCount is set for session series only.</summary>
public sealed record HistoryPointDto(
    DateTime TimestampUtc, float CpuCores, long PrivateBytes, long ReadBytesDelta, long WriteBytesDelta, int? ProcessCount);

/// <summary>
/// A time series for one session unit (UnitId set) or one process (Key set). Name is the session's display title or the
/// process image name when known. Idle processes are sampled every 60 s on disk, active ones every tick.
/// </summary>
public sealed record HistorySeriesDto(
    string? UnitId, string? Key, string? Name, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<HistoryPointDto> Points);

/// <summary>
/// Service health. LastTickUtc and TickAgeSeconds are null before the collector's first tick. Mode is how the instance
/// runs: "service", "user" or "dev". Owner is the DOMAIN\user an instance runs as in user mode, and omitted otherwise.
/// Both are null when read from a service that predates them.
/// </summary>
public sealed record HealthDto(
    string Version, DateTime StartedUtc, DateTime? LastTickUtc, double? TickAgeSeconds, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner);

/// <summary>A kill performed by a client. TsUtc defaults to the time the service receives the record.</summary>
public sealed record KillAuditDto(
    string Key, string ImageName, string? UnitId, string CallerImage, bool Tree, string Result, DateTime? TsUtc = null);
