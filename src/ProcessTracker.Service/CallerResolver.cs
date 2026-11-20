using System.Text.RegularExpressions;
using ProcessTracker.Core.Model;
using ProcessTracker.Native;

namespace ProcessTracker.Service;

/// <summary>Resolves which session (or app) made an HTTP request.</summary>
public interface ICallerResolver
{
    Attribution? Resolve(HttpContext ctx);
}

/// <summary>
/// Identifies the caller from the TCP connection itself: the process owning the client socket (PID and creation
/// time), then that exact process's attribution in the latest tick. When that is not a session (for instance a
/// desktop-app proxy made the connection, or the caller is younger than the last tick), a well-formed
/// <c>X-Claude-Session</c> header carrying the host session id is trusted instead. That is cheap to trust: the API is
/// read-only and the service has no kill capability.
/// </summary>
public sealed partial class CallerResolver(ITrackerHost host, Func<ConnectionInfo, ProcessKey?> ownerOfClientSocket)
    : ICallerResolver
{
    public const string SessionHeader = "X-Claude-Session";
    private const string HostPrefix = "host:";

    /// <summary>The production resolver, over the machine's TCP table.</summary>
    public CallerResolver(ITrackerHost host) : this(host, OwnerFromTcpTable) { }

    public Attribution? Resolve(HttpContext ctx)
    {
        var found = FromConnection(ctx.Connection);
        if (found?.Kind != AttributionKind.Session
            && ctx.Request.Headers[SessionHeader].FirstOrDefault() is { } hostSessionId
            && HostSessionIdPattern().IsMatch(hostSessionId))
            return new Attribution(AttributionKind.Session, HostPrefix + hostSessionId, null, false);
        return found;
    }

    private Attribution? FromConnection(ConnectionInfo connection)
    {
        // The in-memory test server and non-TCP transports have no ports.
        if (connection.RemotePort == 0 || connection.LocalPort == 0) return null;
        if (ownerOfClientSocket(connection) is not { } owner) return null;

        // Exact identity: a PID reused since the last tick does not match the earlier process still in the view.
        return host.View.Processes.TryGetValue(owner, out var view) ? view.Record.Attribution : null;
    }

    private static ProcessKey? OwnerFromTcpTable(ConnectionInfo c) =>
        c.RemoteIpAddress is { } client && c.LocalIpAddress is { } server
            ? TcpOwnerLookup.OwnerKeyOfLoopbackClient(client, c.RemotePort, server, c.LocalPort)
            : null;

    /// <summary>What a host session id may look like; anything else is treated as no header. (\z, not $, which would allow a trailing newline.)</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,128}\z")]
    private static partial Regex HostSessionIdPattern();
}
