using System.Collections.Frozen;
using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Orphans;
using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tracking;

/// <summary>One live process as of the latest tick.</summary>
public sealed record ProcessView(
    ProcessRecord Record, ProcessSample Latest, ActivityState Activity, DateTime? EffectiveLastActive,
    ProcessState State, bool Alertable);

/// <summary>A session unit with the totals of its live processes in the latest tick.</summary>
public sealed record SessionView(SessionInfo Info, bool IsLive, float CpuCores, long PrivateBytes, int ProcessCount);

/// <summary>Immutable result of one tick. Replaced (never mutated) on every tick, so readers on other threads are safe.</summary>
public sealed record TrackerView(
    DateTime TimestampUtc,
    SystemSample System,
    IReadOnlyDictionary<ProcessKey, ProcessView> Processes,
    IReadOnlyList<SessionView> Sessions)
{
    public static TrackerView Empty { get; } =
        new(DateTime.MinValue, default, FrozenDictionary<ProcessKey, ProcessView>.Empty, Array.Empty<SessionView>());
}
