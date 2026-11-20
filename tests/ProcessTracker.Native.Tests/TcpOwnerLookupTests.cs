using System.Net;
using System.Net.Sockets;

namespace ProcessTracker.Native.Tests;

public class TcpOwnerLookupTests
{
    [Fact] public async Task TcpOwnerLookup_FindsOurOwnClientSocket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
            using var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

            var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Equal(Environment.ProcessId, TcpOwnerLookup.OwnerOfLoopbackClient(clientPort, serverPort));
            Assert.Null(TcpOwnerLookup.OwnerOfLoopbackClient(clientPort, 0)); // no connection has remote port 0
        }
        finally
        {
            listener.Stop();
        }
    }
}
