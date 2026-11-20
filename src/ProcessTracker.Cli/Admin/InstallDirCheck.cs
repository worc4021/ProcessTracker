using System.Security.Principal;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// The install dir becomes the script a SYSTEM task runs daily, so register-task only accepts a fully qualified
/// folder that holds upgrade.ps1 and that only administrators (and SYSTEM / TrustedInstaller) can write to.
/// </summary>
public static class InstallDirCheck
{
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Trims a stray trailing quote or separator (a quoted path ending in a backslash arrives as C:\x").</summary>
    public static string Normalize(string installDir) => installDir.Trim().TrimEnd('"', '\\', '/').Trim();

    /// <summary>Null when acceptable, otherwise the reason it is refused. <paramref name="dir"/> is already normalized.</summary>
    public static string? Validate(string dir, Func<string, bool>? isAdminOnly = null)
    {
        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) return $"--install-dir must be a fully qualified path (got '{dir}').";
        if (!File.Exists(Path.Combine(dir, "upgrade.ps1"))) return $@"{dir}\upgrade.ps1 does not exist.";
        return (isAdminOnly ?? IsAdminOnly)(dir) ? null : $"{dir} is not admin-only (owner or ACL lets non-administrators change it).";
    }

    public static bool IsAdminOnly(string dir)
    {
        using var handle = NativeFolder.OpenForRead(dir, out _);
        if (handle is null) return false;
        var (owner, aces) = NativeFolder.ReadSecurity(handle);
        return DataFolderSetup.Classify(owner, !NativeFolder.IsPlainDirectory(handle), aces, [TrustedInstaller]) == DataFolderSetup.FolderTrust.Trusted;
    }
}
