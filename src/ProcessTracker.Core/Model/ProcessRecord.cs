namespace ProcessTracker.Core.Model;

/// <summary>A tracked process (live or exited) with its attribution.</summary>
public sealed record ProcessRecord(
    ProcessKey Key,
    ProcessKey? Parent,
    string ImageName,
    string? ImagePath,
    string? CommandLine,
    Attribution Attribution,
    DateTime FirstSeenUtc,
    DateTime? ExitedUtc);
