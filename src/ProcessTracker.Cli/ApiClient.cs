using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Load;

namespace ProcessTracker.Cli;

/// <summary>The service did not answer (connection refused, timeout, ...).</summary>
public sealed class ServiceUnreachableException(string message, Exception inner) : Exception(message, inner);

/// <summary>The service answered with an unexpected status or body.</summary>
public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Typed client for the REST API. The base address must be http://127.0.0.1:&lt;port&gt; so that the Host header
/// matches the service's DNS-rebinding guard; no Origin header is ever sent.
/// </summary>
public sealed class ApiClient : IDisposable
{
    public const int ProductionPort = 47811;
    public const int DevPort = 47812;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;

    /// <param name="handler">Test seam; production uses a direct (no proxy) socket handler.</param>
    public ApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseUrl), Timeout = RequestTimeout };

        // Helps the service's own view of the caller; the kill guard does not depend on it (see CallerIdentity).
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_HOST_SESSION_ID") is { Length: > 0 } hostSessionId)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Claude-Session", hostSessionId);
    }

    public static string ResolveBaseUrl(bool dev) =>
        Environment.GetEnvironmentVariable("PROCTRACK_URL") is { Length: > 0 } url
            ? url
            : $"http://127.0.0.1:{(dev ? DevPort : ProductionPort)}";

    public Task<HealthDto> HealthAsync(CancellationToken ct) => RequiredAsync<HealthDto>("/api/health", ct);
    public Task<LoadReport> LoadAsync(CancellationToken ct) => RequiredAsync<LoadReport>("/api/load", ct);
    public Task<SessionViewDto[]> SessionsAsync(CancellationToken ct) => RequiredAsync<SessionViewDto[]>("/api/sessions", ct);
    public Task<SessionTreeDto?> SessionTreeAsync(string unit, CancellationToken ct) =>
        GetAsync<SessionTreeDto>($"/api/sessions/{Uri.EscapeDataString(unit)}", ct);
    public Task<ProcessViewDto[]> OrphansAsync(CancellationToken ct) => RequiredAsync<ProcessViewDto[]>("/api/orphans", ct);
    public Task<ProcessViewDto[]> ProcessesByPidAsync(int pid, CancellationToken ct) =>
        RequiredAsync<ProcessViewDto[]>($"/api/processes/by-pid/{pid}", ct);

    /// <summary>The caller as the service sees it; null when the service does not know the caller (JSON null).</summary>
    public Task<AttributionDto?> CallerAsync(CancellationToken ct) => GetAsync<AttributionDto>("/api/caller", ct, notFoundIsNull: false);

    public Task<HistorySeriesDto?> HistoryAsync(string? unit, string? key, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var who = unit is not null ? $"unit={Uri.EscapeDataString(unit)}" : $"key={Uri.EscapeDataString(key!)}";
        var range = $"from={fromUtc:yyyy-MM-ddTHH:mm:ssZ}&to={toUtc:yyyy-MM-ddTHH:mm:ssZ}";
        return GetAsync<HistorySeriesDto>($"/api/history?{who}&{range}", ct);
    }

    public async Task PostKillAuditAsync(KillAuditDto dto, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsJsonAsync("/api/audit/kill", dto, ContractJson.Options, ct), ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>GET a body that must exist: 404 or a JSON null is an unexpected answer.</summary>
    private async Task<T> RequiredAsync<T>(string path, CancellationToken ct) where T : class =>
        await GetAsync<T>(path, ct, notFoundIsNull: false)
        ?? throw new ApiException(HttpStatusCode.OK, $"Service returned an empty answer for {path}.");

    /// <summary>GET and deserialize; null for a JSON null body, and for 404 when <paramref name="notFoundIsNull"/>.</summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct, bool notFoundIsNull = true) where T : class
    {
        using var response = await SendAsync(() => _http.GetAsync(path, ct), ct);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        try { return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct); }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ApiException(response.StatusCode, $"Service returned an unexpected answer for {path}: {e.Message}");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new ServiceUnreachableException($"Connection to the ProcessTracker service was lost: {e.Message}", e);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try { return await send(); }
        catch (HttpRequestException e) { throw new ServiceUnreachableException($"ProcessTracker service is not reachable: {e.Message}", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ServiceUnreachableException("ProcessTracker service did not answer within 5 seconds.", e);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or IOException) { body = e.Message; }
        throw new ApiException(response.StatusCode, $"Service returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
