using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ProcessTracker.Cli.Admin;

public sealed record SetupResult(bool Ok, string Message);

/// <summary>
/// Creates and secures the service's data folder (called by the installer as SYSTEM). Any user can create folders
/// under %ProgramData%, so a pre-planted junction or file must not redirect or survive what SYSTEM writes here.
/// </summary>
public sealed class DataFolderSetup(string dataDir)
{
    private const string SettingsFile = "appsettings.json";

    private string HistoryDir => Path.Combine(dataDir, "history");

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Owner Administrators and a DACL without inheritance: SYSTEM and Administrators full control, Users read and
    /// execute. state.db holds process command lines; the owner matters when the folder already existed, since a user
    /// who created it would otherwise keep owner rights to it.
    /// </summary>
    public static DirectorySecurity BuildSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var (sid, rights) in new[]
        {
            (system, FileSystemRights.FullControl),
            (admins, FileSystemRights.FullControl),
            (users, FileSystemRights.ReadAndExecute),
        })
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    public SetupResult Prepare()
    {
        try
        {
            if (ReparseRefusal() is { } refusal) return new SetupResult(false, refusal);
            if (!IsElevated())
                return new SetupResult(false, "prepare-data must run elevated (as an administrator or SYSTEM) to set the data folder owner. Nothing was changed.");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(HistoryDir);
            // Re-check after creation: closes the window in which a folder could be swapped for a link.
            if (ReparseRefusal() is { } late) return new SetupResult(false, late);

            foreach (var dir in new[] { dataDir, HistoryDir })
                new DirectoryInfo(dir).SetAccessControl(BuildSecurity());

            CarryOverSettings();
            return new SetupResult(true, $"Prepared {dataDir}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return new SetupResult(false, $"Could not prepare {dataDir}: {e.Message}");
        }
    }

    public SetupResult Remove(bool purge)
    {
        try
        {
            if (ReparseRefusal() is { } refusal) return new SetupResult(false, refusal);
            if (!purge) return new SetupResult(true, $"Kept {dataDir} (use --purge to delete it).");
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            return new SetupResult(true, $"Removed {dataDir}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SetupResult(false, $"Could not remove {dataDir}: {e.Message}");
        }
    }

    private string? ReparseRefusal()
    {
        foreach (var dir in new[] { dataDir, HistoryDir })
        {
            var info = new DirectoryInfo(dir);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"{dir} is a junction or symbolic link; remove it and try again. Nothing was changed.";
        }
        return null;
    }

    /// <summary>
    /// Replaces any existing settings with a freshly created file: the old one may carry an attacker's ACL or content,
    /// and a new file inherits the folder's protected ACL.
    /// </summary>
    private void CarryOverSettings()
    {
        var path = Path.Combine(dataDir, SettingsFile);
        string? old = null;
        var info = new FileInfo(path);
        if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try { old = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* unreadable: carry nothing over */ }
        }
        if (info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) File.Delete(path);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(new UTF8Encoding(false).GetBytes(SettingsCarryOver.Rewrite(old)));
    }
}
