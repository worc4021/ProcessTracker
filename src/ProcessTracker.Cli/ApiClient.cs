using System.Net;
using System.Net.Http.Json;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Load;

namespace ProcessTracker.Cli;

/// <summary>The service did not answer (connection refused, timeout, ...).</summary>
public sealed class ServiceUnreachableException(string message, Exception inner) : Exception(message, inner);

/// <summary>The service answered with an unexpected status.</summary>
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

    public ApiClient(string baseUrl)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseUrl), Timeout = RequestTimeout };

        // The service identifies the caller by the process owning the socket; a process younger than the last
        // sampling tick is not known yet, so inside a Claude session the host session id backs that up.
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_HOST_SESSION_ID") is { Length: > 0 } hostSessionId)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Claude-Session", hostSessionId);
    }

    public static string ResolveBaseUrl(bool dev) =>
        Environment.GetEnvironmentVariable("PROCTRACK_URL") is { Length: > 0 } url
            ? url
            : $"http://127.0.0.1:{(dev ? DevPort : ProductionPort)}";

    public Task<LoadReport> LoadAsync(CancellationToken ct) => GetAsync<LoadReport>("/api/load", ct)!;
    public Task<SessionViewDto[]> SessionsAsync(CancellationToken ct) => GetAsync<SessionViewDto[]>("/api/sessions", ct)!;
    public Task<SessionTreeDto?> SessionTreeAsync(string unit, CancellationToken ct) =>
        GetAsync<SessionTreeDto>($"/api/sessions/{Uri.EscapeDataString(unit)}", ct);
    public Task<ProcessViewDto[]> OrphansAsync(CancellationToken ct) => GetAsync<ProcessViewDto[]>("/api/orphans", ct)!;
    public Task<ProcessViewDto[]> ProcessesByPidAsync(int pid, CancellationToken ct) =>
        GetAsync<ProcessViewDto[]>($"/api/processes/by-pid/{pid}", ct)!;
    public Task<AttributionDto?> CallerAsync(CancellationToken ct) => GetAsync<AttributionDto>("/api/caller", ct);

    public Task<HistorySeriesDto?> HistoryAsync(string? unit, string? key, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var who = unit is not null ? $"unit={Uri.EscapeDataString(unit)}" : $"key={Uri.EscapeDataString(key!)}";
        var range = $"from={fromUtc:yyyy-MM-ddTHH:mm:ssZ}&to={toUtc:yyyy-MM-ddTHH:mm:ssZ}";
        return GetAsync<HistorySeriesDto>($"/api/history?{who}&{range}", ct);
    }

    public async Task PostKillAuditAsync(KillAuditDto dto, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsJsonAsync("/api/audit/kill", dto, ContractJson.Options, ct));
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>GET and deserialize; null for 404 or a JSON null body.</summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(() => _http.GetAsync(path, ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try { return await send(); }
        catch (HttpRequestException e) { throw new ServiceUnreachableException($"ProcessTracker service is not reachable: {e.Message}", e); }
        catch (TaskCanceledException e) { throw new ServiceUnreachableException("ProcessTracker service did not answer within 5 seconds.", e); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ApiException(response.StatusCode, $"Service returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
