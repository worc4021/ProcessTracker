using System.Security.Principal;
using ProcessTracker.Native;

namespace ProcessTracker.Service;

/// <summary>Decides whether a request comes from the user the instance runs as (user mode only).</summary>
public interface IRequestOwnerGate
{
    bool IsOwner(HttpContext ctx);
}

/// <summary>
/// The production gate: the process owning the client socket (from the TCP table) must run as this process's user SID.
/// An unknown owner (no ports, a connection not in the table, a process that cannot be opened) is not the owner.
/// </summary>
public sealed class OwnerGate : IRequestOwnerGate
{
    public const string ForbiddenMessage =
        "this ProcessTracker belongs to another user; ask an admin to install the machine-wide service";

    private static readonly SecurityIdentifier Me = CurrentUser();

    public bool IsOwner(HttpContext ctx)
    {
        var c = ctx.Connection;
        // The in-memory test server and non-TCP transports have no ports.
        if (c.RemotePort == 0 || c.LocalPort == 0 || c.RemoteIpAddress is not { } client || c.LocalIpAddress is not { } server)
            return false;
        return TcpOwnerLookup.OwnerOfLoopbackClient(client, c.RemotePort, server, c.LocalPort) is { } pid
               && ProcessOwner.SidOf(pid) is { } sid
               && sid.Equals(Me);
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User!;
    }
}
