using ProcessTracker.Core.Model;
using ProcessTracker.Native;

namespace ProcessTracker.Service;

/// <summary>Resolves which session (or app) made an HTTP request.</summary>
public interface ICallerResolver
{
    Attribution? Resolve(HttpContext ctx);
}

/// <summary>
/// Identifies the caller from the TCP connection itself: the process owning the client socket, then that process's
/// attribution in the latest tick. When that is not a session (for instance a desktop-app proxy made the connection),
/// an <c>X-Claude-Session</c> header carrying the host session id is trusted instead. That is cheap to trust: the API
/// is read-only and the service has no kill capability.
/// </summary>
public sealed class CallerResolver(ITrackerHost host, Func<int, int, int?> ownerOfLoopbackClient) : ICallerResolver
{
    public const string SessionHeader = "X-Claude-Session";
    private const string HostPrefix = "host:";

    /// <summary>The production resolver, over the machine's TCP table.</summary>
    public CallerResolver(ITrackerHost host) : this(host, TcpOwnerLookup.OwnerOfLoopbackClient) { }

    public Attribution? Resolve(HttpContext ctx)
    {
        var found = FromConnection(ctx.Connection);
        if (found?.Kind != AttributionKind.Session
            && ctx.Request.Headers[SessionHeader].FirstOrDefault()?.Trim() is { Length: > 0 } hostSessionId)
            return new Attribution(AttributionKind.Session, HostPrefix + hostSessionId, null, false);
        return found;
    }

    private Attribution? FromConnection(ConnectionInfo connection)
    {
        // The in-memory test server and non-TCP transports have no ports.
        if (connection.RemotePort == 0 || connection.LocalPort == 0) return null;
        if (ownerOfLoopbackClient(connection.RemotePort, connection.LocalPort) is not { } pid) return null;

        // At most one live process has a given PID; prefer the newest should a reused PID still linger in the view.
        return host.View.Processes.Values
            .Where(p => p.Record.Key.Pid == pid)
            .MaxBy(p => p.Record.Key.CreateTimeTicks)
            ?.Record.Attribution;
    }
}
