using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProcessTracker.Core.Updates;

namespace ProcessTracker.Core.Tests.Updates;

public sealed class ReleaseClientTests : IDisposable
{
    private const string Asset = "ProcessTracker-0.3.0-x64.zip";
    private const string AssetUrl = "https://github.com/worc4021/ProcessTracker/releases/download/v0.3.0/" + Asset;
    private const string SumsUrl = "https://github.com/worc4021/ProcessTracker/releases/download/v0.3.0/SHA256SUMS.txt";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static string ReleaseJson(string tag) => $$"""
        {"tag_name":"{{tag}}","assets":[
          {"name":"ProcessTracker-0.3.0-x64.msi","browser_download_url":"https://github.com/x/a.msi"},
          {"name":"{{Asset}}","browser_download_url":"{{AssetUrl}}"},
          {"name":"SHA256SUMS.txt","browser_download_url":"{{SumsUrl}}"}]}
        """;

    private static string HashOf(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ReleaseInfo Release() => new(new Version(0, 3, 0), "v0.3.0", new Dictionary<string, Uri>
    {
        [Asset] = new(AssetUrl),
        ["SHA256SUMS.txt"] = new(SumsUrl),
    });

    private static ReleaseClient Client(HttpMessageHandler h) => new(h, "0.2.0");

    [Fact]
    public async Task ReleaseClient_LatestParsesTagAndAssets()
    {
        var client = Client(new FakeHandler(_ => Ok(ReleaseJson("v0.3.0"))));
        var r = await client.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(r);
        Assert.Equal(new Version(0, 3, 0), r.Version);
        Assert.Equal("v0.3.0", r.Tag);
        Assert.Equal(3, r.Assets.Count);
        Assert.Equal(new Uri(AssetUrl), r.Assets[Asset]);
    }

    [Theory]
    [InlineData("v0.3.0-rc1")]
    [InlineData("0.3.0")]
    [InlineData("v0.3")]
    public async Task ReleaseClient_PrereleaseTag_ReturnsNull(string tag)
    {
        var client = Client(new FakeHandler(_ => Ok(ReleaseJson(tag))));
        Assert.Null(await client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseClient_MissingTag_ReturnsNull()
    {
        var client = Client(new FakeHandler(_ => Ok("""{"assets":[]}""")));
        Assert.Null(await client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReleaseClient_RateLimited403_ReturnsNull(HttpStatusCode code)
    {
        var client = Client(new FakeHandler(_ => new HttpResponseMessage(code)));
        Assert.Null(await client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseClient_SendsUserAgent()
    {
        var handler = new FakeHandler(_ => Ok(ReleaseJson("v0.3.0")));
        await Client(handler).GetLatestAsync(TestContext.Current.CancellationToken);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(ReleaseClient.LatestReleaseUri, req.RequestUri);
        Assert.StartsWith("ProcessTracker/", req.Headers.UserAgent.ToString());
        Assert.Contains("0.2.0", req.Headers.UserAgent.ToString());
    }

    [Fact]
    public void ReleaseClient_AutoRedirectHandler_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseClient(new HttpClientHandler { AllowAutoRedirect = true }, "1"));
        using var ok = new HttpClientHandler { AllowAutoRedirect = false };
        _ = new ReleaseClient(ok, "1");
    }

    [Fact]
    public async Task Download_HashMatches_ReturnsPath()
    {
        var data = Encoding.UTF8.GetBytes("zip-bytes");
        var sums = $"{HashOf(data)}  {Asset}\n";
        var client = Client(new FakeHandler(r => r.RequestUri!.AbsoluteUri == SumsUrl ? Ok(sums) : Ok(data)));

        var path = await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir, Asset), path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_HashMismatch_ReturnsNullAndLeavesNoFiles()
    {
        var sums = $"{HashOf("something else"u8.ToArray())}  {Asset}\n";
        var client = Client(new FakeHandler(r => r.RequestUri!.AbsoluteUri == SumsUrl ? Ok(sums) : Ok("zip-bytes")));

        Assert.Null(await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_SumsMissingAsset_ReturnsNull()
    {
        var sums = $"{HashOf([1])}  other-file.zip\n";
        var handler = new FakeHandler(r => r.RequestUri!.AbsoluteUri == SumsUrl ? Ok(sums) : Ok("zip-bytes"));

        Assert.Null(await Client(handler).DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsoluteUri == AssetUrl);
    }

    [Fact]
    public async Task Download_MalformedSums_ReturnsNull()
    {
        var client = Client(new FakeHandler(_ => Ok("garbage")));
        Assert.Null(await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_RedirectToForeignHost_ReturnsNull()
    {
        var data = "zip-bytes"u8.ToArray();
        var sums = $"{HashOf(data)}  {Asset}\n";
        var handler = new FakeHandler(r => r.RequestUri!.AbsoluteUri switch
        {
            SumsUrl => Ok(sums),
            AssetUrl => Redirect("https://evil.example/x.zip"),
            _ => Ok(data),
        });

        Assert.Null(await Client(handler).DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public async Task Download_RedirectToHttp_ReturnsNull()
    {
        var sums = $"{HashOf([1])}  {Asset}\n";
        var client = Client(new FakeHandler(r => r.RequestUri!.AbsoluteUri switch
        {
            SumsUrl => Ok(sums),
            _ => Redirect("http://github.com/x.zip"),
        }));
        Assert.Null(await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_RedirectToReleaseAssetsHost_Followed()
    {
        var data = "zip-bytes"u8.ToArray();
        var sums = $"{HashOf(data)}  {Asset}\n";
        const string cdn = "https://release-assets.githubusercontent.com/blob/x";
        var client = Client(new FakeHandler(r => r.RequestUri!.AbsoluteUri switch
        {
            SumsUrl => Ok(sums),
            AssetUrl => Redirect(cdn),
            cdn => Ok(data),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var path = await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken);
        Assert.NotNull(path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_TooManyRedirects_ReturnsNull()
    {
        var sums = $"{HashOf([1])}  {Asset}\n";
        var client = Client(new FakeHandler(r => r.RequestUri!.AbsoluteUri == SumsUrl
            ? Ok(sums)
            : Redirect("https://github.com/loop")));
        Assert.Null(await client.DownloadVerifiedAsync(Release(), Asset, _dir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_AssetNameWithPath_ReturnsNull()
    {
        var release = new ReleaseInfo(new Version(0, 3, 0), "v0.3.0", new Dictionary<string, Uri>
        {
            ["..\\evil.zip"] = new(AssetUrl),
            ["SHA256SUMS.txt"] = new(SumsUrl),
        });
        var client = Client(new FakeHandler(_ => Ok("x")));
        Assert.Null(await client.DownloadVerifiedAsync(release, "..\\evil.zip", _dir, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IsNewer_StrictlyGreater()
    {
        Assert.True(ReleaseClient.IsNewer(new Version(0, 3, 0), new Version(0, 2, 9)));
        Assert.False(ReleaseClient.IsNewer(new Version(0, 2, 0), new Version(0, 2, 0)));
        Assert.False(ReleaseClient.IsNewer(new Version(0, 1, 9), new Version(0, 2, 0)));
    }
}
