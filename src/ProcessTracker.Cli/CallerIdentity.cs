using ProcessTracker.Core.Model;

namespace ProcessTracker.Cli;

/// <summary>
/// Combines what the service says about the caller with the caller's own environment. This is an accident guard
/// (an agent killing the wrong thing), not a security boundary: an agent can clear its environment or use
/// Stop-Process directly. Inside a Claude session the environment wins over a service answer that is missing
/// because this short-lived process was not yet sampled.
/// </summary>
public static class CallerIdentity
{
    public static Attribution? Resolve(Attribution? fromService, string? hostSessionIdEnv, string? sessionIdEnv)
    {
        var sessionId = string.IsNullOrEmpty(sessionIdEnv) ? null : sessionIdEnv;
        if (!string.IsNullOrEmpty(hostSessionIdEnv))
            return new Attribution(AttributionKind.Session, "host:" + hostSessionIdEnv, sessionId, false);

        if (sessionId is not null)
        {
            // Unit unknown: UnitId stays null so that KillGuard fails closed.
            return fromService is { Kind: AttributionKind.Session, UnitId: not null }
                ? fromService
                : new Attribution(AttributionKind.Session, null, sessionId, false);
        }
        return fromService;
    }
}
