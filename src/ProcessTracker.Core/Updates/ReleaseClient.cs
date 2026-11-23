using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcessTracker.Core.Updates;

/// <summary>
/// Reads the latest ProcessTracker GitHub release and downloads its assets with hash verification.
/// This is the security gate for unattended installs: <see cref="DownloadVerifiedAsync"/> is the single place
/// where verification happens (a signature check will be added there once releases are signed).
/// </summary>
/// <remarks>
/// The caller supplies the handler and it MUST NOT follow redirects itself (<c>AllowAutoRedirect = false</c>);
/// redirects are followed here, at most <see cref="MaxRedirects"/>, only over https and only to allowlisted hosts.
/// A <see cref="SocketsHttpHandler"/> or <see cref="HttpClientHandler"/> with auto-redirect enabled is rejected.
/// </remarks>
public sealed partial class ReleaseClient
{
    /// <summary>The only release API endpoint ever queried.</summary>
    public static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/worc4021/ProcessTracker/releases/latest");

    /// <summary>Name of the release asset listing the SHA-256 of every other asset.</summary>
    public const string SumsAssetName = "SHA256SUMS.txt";

    /// <summary>Maximum number of manual redirects followed per request.</summary>
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    private readonly HttpClient _http;

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex TagPattern();

    /// <summary>Creates a client over <paramref name="handler"/> (which must not auto-follow redirects).</summary>
    /// <param name="handler">Message handler; tests pass a fake.</param>
    /// <param name="userAgentVersion">Version sent as <c>User-Agent: ProcessTracker/&lt;version&gt;</c>.</param>
    public ReleaseClient(HttpMessageHandler handler, string userAgentVersion)
    {
        if (handler is SocketsHttpHandler { AllowAutoRedirect: true } or HttpClientHandler { AllowAutoRedirect: true })
            throw new ArgumentException("The handler must have AllowAutoRedirect = false.", nameof(handler));
        _http = new HttpClient(handler, disposeHandler: false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ProcessTracker/{userAgentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>True only when <paramref name="candidate"/> is strictly greater than <paramref name="installed"/>.</summary>
    public static bool IsNewer(Version candidate, Version installed) => candidate > installed;

    /// <summary>
    /// Fetches the latest release. Returns null on 403/404/5xx, network failure, malformed JSON, a missing tag or a
    /// tag that is not <c>vX.Y.Z</c> (pre-releases are ignored).
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendFollowingRedirectsAsync(LatestReleaseUri, allowApiHost: true, ct);
            if (response is null || !response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
                return null;
            var tag = tagEl.GetString();
            if (tag is null || !TagPattern().IsMatch(tag)) return null;

            var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assetsEl.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    if (!a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)) continue;
                    assets[n.GetString()!] = uri;
                }
            }
            return new ReleaseInfo(Version.Parse(tag[1..]), tag, assets);
        }
        catch (Exception e) when (IsTransient(e, ct) || e is JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads <c>SHA256SUMS.txt</c> and <paramref name="assetName"/> into <paramref name="targetDir"/>.
    /// Returns the asset's path only when its SHA-256 matches the sums file; on any failure both files are deleted
    /// and null is returned, so nothing unverified is left behind.
    /// </summary>
    public async Task<string?> DownloadVerifiedAsync(
        ReleaseInfo release, string assetName, string targetDir, CancellationToken ct)
    {
        // Names come from remote JSON; refuse anything that could escape targetDir.
        if (!IsPlainFileName(assetName)) return null;
        if (!release.Assets.TryGetValue(SumsAssetName, out var sumsUri)
            || !release.Assets.TryGetValue(assetName, out var assetUri))
            return null;

        Directory.CreateDirectory(targetDir);
        var sumsPath = Path.Combine(targetDir, SumsAssetName);
        var assetPath = Path.Combine(targetDir, assetName);
        var verified = false;
        try
        {
            if (!await DownloadToFileAsync(sumsUri, sumsPath, ct)) return null;

            IReadOnlyDictionary<string, string> sums;
            try { sums = Sha256Sums.Parse(await File.ReadAllTextAsync(sumsPath, ct)); }
            catch (FormatException) { return null; }
            if (!sums.TryGetValue(assetName, out var expected)) return null;

            if (!await DownloadToFileAsync(assetUri, assetPath, ct)) return null;

            string actual;
            await using (var fs = File.OpenRead(assetPath))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) return null;

            verified = true;
            return assetPath;
        }
        catch (Exception e) when (IsTransient(e, ct) || e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (!verified)
            {
                TryDelete(sumsPath);
                TryDelete(assetPath);
            }
        }
    }

    // Network failures and timeouts are "no result"; caller-requested cancellation still propagates.
    private static bool IsTransient(Exception e, CancellationToken ct) =>
        e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name) && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private async Task<bool> DownloadToFileAsync(Uri uri, string path, CancellationToken ct)
    {
        using var response = await SendFollowingRedirectsAsync(uri, allowApiHost: false, ct);
        if (response is null || !response.IsSuccessStatusCode) return false;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
        return true;
    }

    /// <summary>
    /// Sends a GET, following redirects manually. Every hop (including the first) must be https on an allowed host;
    /// otherwise returns null. Also null when the redirect limit is exceeded.
    /// </summary>
    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(Uri uri, bool allowApiHost, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowed(current, allowApiHost)) return null;
            var response = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, current), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) return null;
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        return null;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsAllowed(Uri uri, bool allowApiHost) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (AllowedDownloadHosts.Contains(uri.Host)
            || (allowApiHost && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
