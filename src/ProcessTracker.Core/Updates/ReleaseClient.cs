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

    private const string ReleaseDownloadPrefix = "https://github.com/worc4021/ProcessTracker/releases/download/";

    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    private readonly HttpClient _http;

    [GeneratedRegex(@"^v[0-9]+\.[0-9]+\.[0-9]+\z")]
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
    }

    /// <summary>True only when <paramref name="candidate"/> is strictly greater than <paramref name="installed"/>.</summary>
    public static bool IsNewer(Version candidate, Version installed) => candidate > installed;

    /// <summary>
    /// Fetches the latest release. Returns null on 403/404/5xx, network or I/O failure, malformed JSON, a missing
    /// tag or a tag that is not <c>vX.Y.Z</c> (pre-releases are ignored).
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendFollowingRedirectsAsync(LatestReleaseUri, isApi: true, ct);
            if (response is null || !response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
                return null;
            var tag = tagEl.GetString();
            if (tag is null || !TagPattern().IsMatch(tag) || !Version.TryParse(tag[1..], out var version))
                return null;

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
            return new ReleaseInfo(version, tag, assets);
        }
        catch (Exception e) when (IsTransient(e, ct) || e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads <c>SHA256SUMS.txt</c> and <paramref name="assetName"/> into <paramref name="targetDir"/>.
    /// Returns the asset's path only when its SHA-256 matches the sums file. Both files are streamed to
    /// <c>.partial</c> names and renamed to their final names only after verification, so an unverified file is
    /// never present at a final name. On failure only files created by this call are deleted; pre-existing files
    /// are never touched. Asset URLs outside this repository's release downloads are refused without a request.
    /// </summary>
    public async Task<string?> DownloadVerifiedAsync(
        ReleaseInfo release, string assetName, string targetDir, CancellationToken ct)
    {
        // Names come from remote JSON; refuse anything that could escape targetDir.
        if (!IsPlainFileName(assetName)) return null;
        if (!release.Assets.TryGetValue(SumsAssetName, out var sumsUri)
            || !release.Assets.TryGetValue(assetName, out var assetUri))
            return null;
        if (!IsPinnedReleaseUri(sumsUri) || !IsPinnedReleaseUri(assetUri)) return null;

        var sumsPath = Path.Combine(targetDir, SumsAssetName);
        var assetPath = Path.Combine(targetDir, assetName);
        var sumsPartial = sumsPath + ".partial";
        var assetPartial = assetPath + ".partial";
        var assetRenamed = false;
        var verified = false;
        try
        {
            Directory.CreateDirectory(targetDir);
            if (await DownloadToFileAsync(sumsUri, sumsPartial, ct) is null) return null;

            IReadOnlyDictionary<string, string> sums;
            try { sums = Sha256Sums.Parse(await File.ReadAllTextAsync(sumsPartial, ct)); }
            catch (FormatException) { return null; }
            if (!sums.TryGetValue(assetName, out var expected)) return null;

            var actual = await DownloadToFileAsync(assetUri, assetPartial, ct);
            if (actual is null || !string.Equals(actual, expected, StringComparison.Ordinal)) return null;

            File.Move(assetPartial, assetPath, overwrite: true);
            assetRenamed = true;
            File.Move(sumsPartial, sumsPath, overwrite: true);

            verified = true;
            return assetPath;
        }
        catch (Exception e) when (IsTransient(e, ct) || e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            TryDelete(sumsPartial);
            TryDelete(assetPartial);
            if (!verified && assetRenamed) TryDelete(assetPath);
        }
    }

    private static bool IsPinnedReleaseUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.AbsoluteUri.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal);

    // Network failures and timeouts are "no result"; caller-requested cancellation still propagates.
    private static bool IsTransient(Exception e, CancellationToken ct) =>
        e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name) && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Streams the response to <paramref name="path"/> and returns its lower-case SHA-256, or null on HTTP failure.</summary>
    private async Task<string?> DownloadToFileAsync(Uri uri, string path, CancellationToken ct)
    {
        using var response = await SendFollowingRedirectsAsync(uri, isApi: false, ct);
        if (response is null || !response.IsSuccessStatusCode) return null;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Sends a GET, following redirects manually. Every hop (including the first) must be https on an allowed host;
    /// otherwise returns null. Also null when the redirect limit is exceeded. The GitHub <c>Accept</c> header is
    /// sent only for the API request.
    /// </summary>
    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(Uri uri, bool isApi, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowed(current, isApi)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (isApi) request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
