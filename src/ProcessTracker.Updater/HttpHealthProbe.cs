using System.Text.Json;

namespace ProcessTracker.Updater;

/// <summary>Reads <c>/api/health</c> (camelCase JSON with <c>version</c> and <c>tickAgeSeconds</c>).</summary>
public sealed class HttpHealthProbe(Uri healthUri) : IHealthProbe, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };

    /// <inheritdoc />
    /// <remarks>A missing tick age (no collector tick yet) is reported as infinitely old.</remarks>
    public async Task<HealthResult?> GetAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(healthUri));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                return null;
            var age = root.TryGetProperty("tickAgeSeconds", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetDouble()
                : double.PositiveInfinity;
            return new HealthResult(version.GetString()!, age);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
