using System.Net;
using System.Text;
using ProcessTracker.Cli;
using ProcessTracker.Cli.Commands;

namespace ProcessTracker.Cli.Tests;

public class ApiClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static ApiClient Client(HttpStatusCode status, string body, string type = "application/json") =>
        new("http://127.0.0.1:1", new Stub(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) }));

    [Fact]
    public async Task ListEndpoint_Http404_IsApiException() =>
        await Assert.ThrowsAsync<ApiException>(() => Client(HttpStatusCode.NotFound, "").OrphansAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task ListEndpoint_JsonNull_IsApiException() =>
        await Assert.ThrowsAsync<ApiException>(() => Client(HttpStatusCode.OK, "null").SessionsAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task HtmlBody_IsApiException() =>
        await Assert.ThrowsAsync<ApiException>(() => Client(HttpStatusCode.OK, "<html>", "text/html").LoadAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Caller_JsonNull_IsNull() =>
        Assert.Null(await Client(HttpStatusCode.OK, "null").CallerAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task ConnectionFailure_IsUnreachable()
    {
        var c = new ApiClient("http://127.0.0.1:1", new Stub(_ => throw new HttpRequestException("refused")));
        await Assert.ThrowsAsync<ServiceUnreachableException>(() => c.OrphansAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_IsNotReportedAsTimeout()
    {
        var c = new ApiClient("http://127.0.0.1:1", new Stub(_ => throw new TaskCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.OrphansAsync(cts.Token));
    }

    [Fact]
    public async Task Execute_MapsFailuresToExitCodes()
    {
        Assert.Equal(1, await CommandSupport.ExecuteAsync(() => new ApiClient("not a url"), _ => Task.FromResult(0)));
        Assert.Equal(2, await CommandSupport.ExecuteAsync(() => Client(HttpStatusCode.OK, "[]"),
            _ => throw new ServiceUnreachableException("x", new IOException())));
        Assert.Equal(1, await CommandSupport.ExecuteAsync(() => Client(HttpStatusCode.OK, "[]"),
            _ => throw new System.Text.Json.JsonException("bad")));
    }
}
