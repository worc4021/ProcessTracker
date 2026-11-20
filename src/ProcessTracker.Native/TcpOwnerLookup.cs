using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ProcessTracker.Native;

/// <summary>Finds which process owns the client end of a loopback TCP connection (IPv4 only, as the service binds 127.0.0.1).</summary>
public static class TcpOwnerLookup
{
    private const int MaxAttempts = 5; // the table can grow between the size query and the read

    /// <summary>
    /// The PID owning the TCP connection whose local port is <paramref name="clientPort"/> and whose remote end is
    /// loopback port <paramref name="serverPort"/>, or null if there is none or the table cannot be read.
    /// </summary>
    public static unsafe int? OwnerOfLoopbackClient(int clientPort, int serverPort)
    {
        if (clientPort is <= 0 or > ushort.MaxValue || serverPort is <= 0 or > ushort.MaxValue) return null;

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
            if (rc != 0) return null;
            return Find(buffer, (ushort)clientPort, (ushort)serverPort);
        }
        return null;
    }

    private static int? Find(ReadOnlySpan<byte> table, ushort clientPort, ushort serverPort)
    {
        var count = MemoryMarshal.Read<uint>(table);
        var rows = MemoryMarshal.Cast<byte, NativeMethods.MibTcpRowOwnerPid>(table[sizeof(uint)..]);
        if (count > (uint)rows.Length) return null;

        foreach (var row in rows[..(int)count])
        {
            if (Port(row.LocalPort) == clientPort && Port(row.RemotePort) == serverPort
                && IsLoopback(row.LocalAddr) && IsLoopback(row.RemoteAddr))
                return (int)row.OwningPid;
        }
        return null;
    }

    /// <summary>Ports are in network byte order in the low 16 bits.</summary>
    private static ushort Port(uint raw) => BinaryPrimitives.ReverseEndianness((ushort)raw);

    /// <summary>Addresses are in network byte order, so the first octet is the low byte: 127.0.0.0/8.</summary>
    private static bool IsLoopback(uint addr) => (addr & 0xFF) == 127;
}
