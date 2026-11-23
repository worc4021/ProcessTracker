using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProcessTracker.Service.Updates;

namespace ProcessTracker.Service.Tests.Updates;

/// <summary>Records what would have been started; starts nothing.</summary>
public sealed class FakeStarter : IProcessStarter
{
    public List<(string Exe, IReadOnlyList<string> Args, string WorkingDirectory)> Started { get; } = [];

    /// <summary>Thrown from <see cref="Start"/> when set (an exe that cannot be started).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Called before recording, e.g. to note the order of events.</summary>
    public Action? OnStart { get; set; }

    public void Start(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        OnStart?.Invoke();
        if (Throw is not null) throw Throw;
        Started.Add((exe, args.ToList(), workingDirectory));
    }
}

/// <summary>Serves a fake GitHub "latest release" with a zip asset and its SHA256SUMS.txt; no network.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    private const string Base = "https://github.com/worc4021/ProcessTracker/releases/download/";

    public string Tag { get; set; } = "v0.3.0";

    /// <summary>The zip served as ProcessTracker-&lt;v&gt;-x64.zip.</summary>
    public byte[] Zip { get; set; } = BuildZip(("ProcessTracker.Service.exe", "new exe"), ("bin/proctrack.exe", "cli"));

    /// <summary>When set, SHA256SUMS.txt lists this hash instead of the zip's real one.</summary>
    public string? SumsOverride { get; set; }

    /// <summary>When true the latest-release request fails (rate limit).</summary>
    public bool ApiFails { get; set; }

    /// <summary>When true the zip's body never arrives (a stalled transfer): reads block until cancelled.</summary>
    public bool StallZip { get; set; }

    public List<Uri> Requests { get; } = [];

    private string Version => Tag[1..];
    private string ZipName => $"ProcessTracker-{Version}-x64.zip";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        lock (Requests) Requests.Add(uri);
        if (uri.Host == "api.github.com")
        {
            if (ApiFails) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var json = $$"""
                {"tag_name":"{{Tag}}","assets":[
                  {"name":"{{ZipName}}","browser_download_url":"{{Base}}{{Tag}}/{{ZipName}}"},
                  {"name":"SHA256SUMS.txt","browser_download_url":"{{Base}}{{Tag}}/SHA256SUMS.txt"}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        if (uri.AbsoluteUri.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
        {
            var hash = SumsOverride ?? Convert.ToHexStringLower(SHA256.HashData(Zip));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {ZipName}\n") });
        }
        if (uri.AbsoluteUri.EndsWith("/" + ZipName, StringComparison.Ordinal))
        {
            HttpContent content = StallZip ? new StreamContent(new StalledStream()) : new ByteArrayContent(Zip);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }
}

/// <summary>A response body whose reads never complete until they are cancelled.</summary>
public sealed class StalledStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
}

/// <summary>A temporary folder deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pt-update-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        // Link-safe: Directory.Delete throws on a junction inside the tree (non-admin), hiding a test's real failure.
        AppFolders.Delete(Path);
    }
}
