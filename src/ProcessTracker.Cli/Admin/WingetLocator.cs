using System.Text.RegularExpressions;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// Finds winget.exe the way SYSTEM has to: the per-user alias is not on SYSTEM's PATH, so pick the newest
/// Microsoft.DesktopAppInstaller_&lt;version&gt;_x64__8wekyb3d8bbwe package directory under WindowsApps.
/// Keep in step with the resolution in installer/upgrade.ps1.
/// </summary>
public static partial class WingetLocator
{
    [GeneratedRegex(@"^Microsoft\.DesktopAppInstaller_(?<v>[^_]+)_x64__8wekyb3d8bbwe$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageDir();

    public static string? Pick(IEnumerable<string> candidateDirs)
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (var dir in candidateDirs)
        {
            var m = PackageDir().Match(Path.GetFileName(dir.TrimEnd('\\', '/')));
            if (!m.Success || !Version.TryParse(m.Groups["v"].Value, out var v)) continue;
            if (bestVersion is null || v > bestVersion) { best = dir; bestVersion = v; }
        }
        return best is null ? null : Path.Combine(best, "winget.exe");
    }

    /// <summary>Enumerates the WindowsApps folder (denied for non-admin users: returns null then).</summary>
    public static string? Locate()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        try { return Pick(Directory.EnumerateDirectories(Path.Combine(programFiles, "WindowsApps"))); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return null; }
    }
}
