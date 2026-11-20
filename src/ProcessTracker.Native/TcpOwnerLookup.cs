using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Native;

/// <summary>Finds which process owns the client end of a loopback TCP connection (IPv4 only, as the service binds 127.0.0.1).</summary>
public static class TcpOwnerLookup
{
    private const int MaxAttempts = 5; // the table can grow between the size query and the read

    /// <summary>
    /// The PID owning the established TCP connection from <paramref name="clientAddress"/>:<paramref name="clientPort"/>
    /// to loopback <paramref name="serverAddress"/>:<paramref name="serverPort"/> (all four compared exactly), or null
    /// if there is none or the table cannot be read.
    /// </summary>
    public static int? OwnerOfLoopbackClient(IPAddress clientAddress, int clientPort, IPAddress serverAddress, int serverPort)
    {
        if (clientPort is <= 0 or > ushort.MaxValue || serverPort is <= 0 or > ushort.MaxValue) return null;
        if (Ipv4(clientAddress) is not { } client || Ipv4(serverAddress) is not { } server) return null;
        if ((server & 0xFF) != 127) return null; // network byte order: the first octet is the low byte
        return ReadTable() is { } table ? Find(table, client, (ushort)clientPort, server, (ushort)serverPort) : null;
    }

    /// <summary>
    /// The owner as a full process identity (PID and creation time), so a PID reused since the tracker's last tick
    /// cannot be mistaken for the earlier process; null if there is no owner or it cannot be opened.
    /// </summary>
    public static ProcessKey? OwnerKeyOfLoopbackClient(IPAddress clientAddress, int clientPort, IPAddress serverAddress, int serverPort) =>
        OwnerOfLoopbackClient(clientAddress, clientPort, serverAddress, serverPort) is { } pid && CreateTime(pid) is { } created
            ? new ProcessKey(pid, created)
            : null;

    private static unsafe byte[]? ReadTable()
    {
        int size = 0;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var buffer = new byte[Math.Max(size, sizeof(uint))];
            uint rc;
            fixed (byte* p = buffer)
            {
                size = buffer.Length;
                rc = NativeMethods.GetExtendedTcpTable(p, ref size, order: false, NativeMethods.AfInet,
                    NativeMethods.TcpTableOwnerPidConnections, 0);
            }
            if (rc == NativeMethods.ErrorInsufficientBuffer) continue; // size now holds the required length
            return rc == 0 ? buffer : null;
        }
        return null;
    }

    private static int? Find(ReadOnlySpan<byte> table, uint clientAddr, ushort clientPort, uint serverAddr, ushort serverPort)
    {
        var count = MemoryMarshal.Read<uint>(table);
        var rows = MemoryMarshal.Cast<byte, NativeMethods.MibTcpRowOwnerPid>(table[sizeof(uint)..]);
        if (count > (uint)rows.Length) return null;

        foreach (var row in rows[..(int)count])
        {
            if (row.State == NativeMethods.MibTcpStateEstablished
                && row.LocalAddr == clientAddr && Port(row.LocalPort) == clientPort
                && row.RemoteAddr == serverAddr && Port(row.RemotePort) == serverPort)
                return (int)row.OwningPid;
        }
        return null;
    }

    /// <summary>The address as the table stores it (network byte order), or null if it is not IPv4.</summary>
    private static uint? Ipv4(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) ? MemoryMarshal.Read<uint>(bytes) : null;
    }

    /// <summary>Ports are in network byte order in the low 16 bits.</summary>
    private static ushort Port(uint raw) => BinaryPrimitives.ReverseEndianness((ushort)raw);

    /// <summary>The process's creation time (FILETIME UTC ticks, as in <see cref="ProcessKey"/>), or null if it cannot be opened.</summary>
    private static long? CreateTime(int pid)
    {
        nint handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return null;
        try
        {
            return NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _) ? created : null;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }
}
