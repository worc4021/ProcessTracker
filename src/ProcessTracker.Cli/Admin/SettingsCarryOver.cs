using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// Rebuilds appsettings.json for an upgrade from an existing (possibly attacker-planted) file: only the settings
/// the service understands as per-machine choices survive, and only when valid.
/// </summary>
public static partial class SettingsCarryOver
{
    private const double MaxRetentionHours = 24 * 30;
    private const double MaxOrphanIdleAlertMinutes = 7 * 24 * 60;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Rewrite(string? existingJson)
    {
        var kept = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (Number(doc.RootElement, "RetentionHours", MaxRetentionHours) is { } hours) kept["RetentionHours"] = hours;
                    if (Number(doc.RootElement, "OrphanIdleAlertMinutes", MaxOrphanIdleAlertMinutes) is { } minutes)
                        kept["OrphanIdleAlertMinutes"] = minutes;
                    if (ProfilePath(doc.RootElement) is { } profile) kept["UserProfilePath"] = profile;
                }
            }
            catch (JsonException) { /* unparseable: carry nothing over */ }
        }
        return JsonSerializer.Serialize(kept, Indented);
    }

    /// <summary>A number in (0, <paramref name="max"/>]; the maxima mirror ServiceOptions (MaxRetentionHours, MaxOrphanIdleAlertMinutes) and must stay in sync.</summary>
    private static double? Number(JsonElement root, string name, double max) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v)
        && double.IsFinite(v) && v > 0 && v <= max
            ? v
            : null;

    /// <summary>
    /// Only a drive-letter rooted path (C:\...). ServiceOptions does not validate UserProfilePath, so this is the only
    /// gate against the SYSTEM service being pointed at an SMB server, in any UNC spelling (\\s\x, /\s\x, \??\UNC\s\x).
    /// </summary>
    private static string? ProfilePath(JsonElement root)
    {
        if (!root.TryGetProperty("UserProfilePath", out var e) || e.ValueKind != JsonValueKind.String) return null;
        var path = e.GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        return DriveRoot().IsMatch(Path.GetPathRoot(path) ?? "") ? path : null;
    }

    [GeneratedRegex(@"^[A-Za-z]:\\$")]
    private static partial Regex DriveRoot();
}
