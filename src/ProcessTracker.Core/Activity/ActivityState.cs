namespace ProcessTracker.Core.Activity;

/// <summary>Last time each activity signal fired for a process.</summary>
public readonly record struct ActivityState(
    DateTime? LastCpuAt, DateTime? LastIoAt, DateTime? LastWriteAt, DateTime? LastWakeAt)
{
    public DateTime? LastActiveAt => Max(Max(LastCpuAt, LastIoAt), Max(LastWriteAt, LastWakeAt));

    internal static DateTime? Max(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : (a >= b ? a : b);
}
