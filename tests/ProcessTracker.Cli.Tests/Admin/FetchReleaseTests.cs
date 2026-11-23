using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProcessTracker.Cli.Admin;
using ProcessTracker.Core.Updates;

namespace ProcessTracker.Cli.Tests.Admin;

/// <summary>The release client runs over a fake handler: no request leaves the machine.</summary>
public sealed class FetchReleaseTests : IDisposable
{
    private const string Msi = "ProcessTracker-0.3.0-x64.msi";
    private const string Download = "https://github.com/worc4021/ProcessTracker/releases/download/v0.3.0/";
    private static readonly byte[] MsiBytes = Encoding.UTF8.GetBytes("msi-bytes");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private const string ReleaseJson = $$"""
        {"tag_name":"v0.3.0","assets":[
          {"name":"{{Msi}}","browser_download_url":"{{Download}}{{Msi}}"},
          {"name":"SHA256SUMS.txt","browser_download_url":"{{Download}}SHA256SUMS.txt"}]}
        """;

    private static FakeHandler GitHub(byte[] msiBody) => new(r => r.RequestUri!.AbsoluteUri switch
    {
        var u when u == ReleaseClient.LatestReleaseUri.AbsoluteUri => Ok(ReleaseJson),
        Download + "SHA256SUMS.txt" => Ok($"{Convert.ToHexStringLower(SHA256.HashData(MsiBytes))}  {Msi}\n"),
        Download + Msi => Ok(msiBody),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    private FetchRelease Fetch(HttpMessageHandler handler, string installed) =>
        new(new ReleaseClient(handler, installed), Version.Parse(installed), _out, _err);

    [Fact]
    public async Task FetchRelease_NotNewer_Exits3()
    {
        var handler = GitHub(MsiBytes);

        var code = await Fetch(handler, "0.3.0").RunAsync("msi", _dir, CancellationToken.None);

        Assert.Equal(3, code);
        Assert.Equal([ReleaseClient.LatestReleaseUri], handler.Requests); // nothing downloaded
        Assert.Equal("", _out.ToString());
    }

    [Fact]
    public async Task FetchRelease_Verified_PrintsPath()
    {
        var code = await Fetch(GitHub(MsiBytes), "0.2.0").RunAsync("msi", _dir, CancellationToken.None);

        Assert.Equal(0, code);
        var lastLine = _out.ToString().TrimEnd().Split(Environment.NewLine)[^1];
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), Msi), lastLine);
        Assert.Equal(MsiBytes, File.ReadAllBytes(lastLine));
    }

    [Fact]
    public async Task FetchRelease_HashMismatch_Exits1()
    {
        var code = await Fetch(GitHub(Encoding.UTF8.GetBytes("tampered")), "0.2.0").RunAsync("msi", _dir, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal("", _out.ToString());
        Assert.False(File.Exists(Path.Combine(_dir, Msi)));
    }

    [Fact]
    public async Task FetchRelease_NoRelease_Exits1()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var code = await Fetch(handler, "0.2.0").RunAsync("msi", _dir, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal("", _out.ToString());
    }

    [Theory]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.3.0-dev.202611231200", "0.3.0")]
    [InlineData("1.2.3+abc", "1.2.3")]
    public void FetchRelease_NumericVersion(string informational, string expected)
    {
        Assert.Equal(Version.Parse(expected), FetchRelease.NumericVersion(informational));
    }

    [Fact]
    public void FetchRelease_NumericVersion_RejectsGarbage()
    {
        Assert.Null(FetchRelease.NumericVersion("not-a-version"));
    }
}
