using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ProcessTracker.Native.Tests;

public class TcpOwnerLookupTests
{
    private static readonly IPAddress Lo = IPAddress.Loopback;

    private static int FreePort()
    {
        var l = new TcpListener(Lo, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact] public async Task TcpOwnerLookup_FindsOurOwnClientSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(Lo, 0);
        listener.Start();
        try
        {
            var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(Lo, serverPort, ct);
            using var server = await listener.AcceptTcpClientAsync(ct);
            var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

            Assert.Equal(Environment.ProcessId, TcpOwnerLookup.OwnerOfLoopbackClient(Lo, clientPort, Lo, serverPort));

            using var me = Process.GetCurrentProcess();
            var key = TcpOwnerLookup.OwnerKeyOfLoopbackClient(Lo, clientPort, Lo, serverPort);
            Assert.Equal(Environment.ProcessId, key?.Pid);
            Assert.Equal(me.StartTime.ToUniversalTime().ToFileTimeUtc(), key?.CreateTimeTicks);

            // Valid ports and addresses that are not this connection's 4-tuple: no match.
            Assert.Null(TcpOwnerLookup.OwnerOfLoopbackClient(Lo, clientPort, Lo, FreePort()));
            Assert.Null(TcpOwnerLookup.OwnerOfLoopbackClient(IPAddress.Parse("127.0.0.2"), clientPort, Lo, serverPort));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact] public async Task TcpOwnerLookup_ReturnsTheClientNotTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(Lo, 0);
        listener.Start();
        var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        // A child process connects and prints its local port, then holds the connection open.
        var script = $"$c = [Net.Sockets.TcpClient]::new('127.0.0.1', {serverPort}); "
                     + "[Console]::Out.WriteLine($c.Client.LocalEndPoint.Port); [Console]::Out.Flush(); Start-Sleep 60";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-Command", script]) psi.ArgumentList.Add(a);
        using var child = Process.Start(psi)!;
        try
        {
            using var accepted = await listener.AcceptTcpClientAsync(ct);
            var line = await child.StandardOutput.ReadLineAsync(ct);
            var clientPort = int.Parse(line!.Trim(), System.Globalization.CultureInfo.InvariantCulture);

            // We own the server end (local serverPort, remote clientPort); the client end is the child's.
            Assert.Equal(child.Id, TcpOwnerLookup.OwnerOfLoopbackClient(Lo, clientPort, Lo, serverPort));
            Assert.Equal(child.Id, TcpOwnerLookup.OwnerKeyOfLoopbackClient(Lo, clientPort, Lo, serverPort)?.Pid);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            listener.Stop();
        }
    }
}
