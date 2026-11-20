using System.Net;
using System.Text;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

/// <summary>DNS-rebinding protection (ruling R7): only loopback Host names on the service port, and no foreign Origin.</summary>
public sealed class RequestGuardTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""";

    private readonly TestApp _app = new(T0);
    private readonly HttpClient _http;

    public RequestGuardTests() => _http = _app.CreateClient(); // every request below uses an absolute URI

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    private async Task<HttpStatusCode> Get(string authority, string path = "/api/health", string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{authority}{path}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> PostMcp(string authority, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{authority}/mcp")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task WrongHost_Is403()
    {
        var port = _app.Port();

        Assert.Equal(HttpStatusCode.Forbidden, await Get($"evil.example:{port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostMcp($"evil.example:{port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get($"localhost:{port + 1}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("localhost")); // no port
        Assert.Equal(HttpStatusCode.Forbidden, await PostMcp("127.0.0.1"));
    }

    [Fact]
    public async Task ForeignOrigin_Is403()
    {
        var port = _app.Port();

        Assert.Equal(HttpStatusCode.Forbidden, await Get($"127.0.0.1:{port}", origin: "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostMcp($"127.0.0.1:{port}", origin: "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get($"localhost:{port}", origin: $"http://localhost:{port + 1}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get($"localhost:{port}", origin: $"https://localhost:{port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get($"localhost:{port}", origin: "null"));
    }

    [Fact]
    public async Task LoopbackHost_Is200()
    {
        var port = _app.Port();

        Assert.Equal(HttpStatusCode.OK, await Get($"127.0.0.1:{port}"));
        Assert.Equal(HttpStatusCode.OK, await Get($"localhost:{port}"));
        Assert.Equal(HttpStatusCode.OK, await PostMcp($"127.0.0.1:{port}"));
        Assert.Equal(HttpStatusCode.OK, await Get($"localhost:{port}", origin: $"http://localhost:{port}"));
        Assert.Equal(HttpStatusCode.OK, await PostMcp($"127.0.0.1:{port}", origin: $"http://127.0.0.1:{port}"));
    }
}
