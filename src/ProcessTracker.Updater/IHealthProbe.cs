namespace ProcessTracker.Updater;

/// <summary>What <c>/api/health</c> reported: the service version and the age of the latest collector tick.</summary>
public sealed record HealthResult(string Version, double TickAgeSeconds);

/// <summary>Reads the service's health endpoint.</summary>
public interface IHealthProbe
{
    /// <summary>The current health, or null when the service does not answer.</summary>
    Task<HealthResult?> GetAsync();
}
