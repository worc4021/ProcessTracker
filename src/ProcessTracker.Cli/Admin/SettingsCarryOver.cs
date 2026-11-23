using System.Text.Json;
using ProcessTracker.Core.Hooks;
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
                    if (Strings(doc.RootElement, "OrphanAlertImages", _ => true) is { } images) kept["OrphanAlertImages"] = images;
                    if (Strings(doc.RootElement, "HeavyCommandPatterns", IsValidPattern) is { } patterns) kept["HeavyCommandPatterns"] = patterns;
                    if (Thresholds(doc.RootElement) is { } thresholds) kept["ActivityThresholds"] = thresholds;
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

    /// <summary>A non-empty array of non-blank strings that all pass <paramref name="valid"/>; anything else drops the key.</summary>
    private static string[]? Strings(JsonElement root, string name, Func<string, bool> valid)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } s || string.IsNullOrWhiteSpace(s) || !valid(s))
                return null;
            list.Add(s);
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    private static bool IsValidPattern(string pattern)
    {
        try { HeavyCommandClassifier.CreatePattern(pattern); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Image name to thresholds; every present field must be a number &gt;= 0 (mirrors ServiceOptions.Validate),
    /// otherwise the whole key is dropped.
    /// </summary>
    private static Dictionary<string, Dictionary<string, double>>? Thresholds(JsonElement root)
    {
        if (!root.TryGetProperty("ActivityThresholds", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        string[] fields = ["CpuCores", "IoOps", "WriteOps", "ContextSwitches"];
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in e.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(image.Name) || image.Value.ValueKind != JsonValueKind.Object) return null;
            var values = new Dictionary<string, double>();
            foreach (var field in image.Value.EnumerateObject())
            {
                if (!fields.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                    || field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var v)
                    || !double.IsFinite(v) || v < 0)
                    return null;
                values[field.Name] = v;
            }
            result[image.Name] = values;
        }
        return result.Count == 0 ? null : result;
    }

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
