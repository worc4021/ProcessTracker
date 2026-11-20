namespace ProcessTracker.Core.Model;

public enum InspectStatus { Ok, AccessDenied, Gone, Unsupported }

/// <summary>Result of inspecting another process. Only allowlisted environment values are ever carried.</summary>
public sealed record InspectResult(
    InspectStatus Status,
    string? SessionId,
    string? HostSessionId,
    string? Entrypoint,
    string? ImagePath,
    string? CommandLine);
