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

    public enum FolderTrust { Trusted, Untrusted }

    /// <summary>An existing folder is kept only when it is not a link and Administrators or SYSTEM own it.</summary>
    public static FolderTrust Classify(SecurityIdentifier? owner, bool isReparse) =>
        !isReparse && owner is not null
        && (owner.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
            || owner.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
            ? FolderTrust.Trusted
            : FolderTrust.Untrusted;

    public SetupResult Prepare()
    {
        try
        {
            if (!IsElevated())
                return new SetupResult(false, "prepare-data must run elevated (as an administrator or SYSTEM) to set the data folder owner. Nothing was changed.");

            // Never adopt an untrusted folder: a user may hold handles (e.g. DELETE) opened while they owned it, and
            // explicit ACEs/owners on pre-planted children survive a DACL change on the parent. Move it aside instead.
            var notes = new List<string>();
            var dataTrusted = Existing(dataDir) && EnsureFolder(dataDir, notes);
            if (!Existing(dataDir)) CreateSecured(dataDir);
            if (Existing(HistoryDir)) EnsureFolder(HistoryDir, notes);
            if (!Existing(HistoryDir)) CreateSecured(HistoryDir);

            var readFailed = false;
            if (dataTrusted)
            {
                // Trusted (Administrators/SYSTEM-owned) folders are re-secured by path; no user can have rights in them.
                foreach (var dir in new[] { dataDir, HistoryDir })
                    new DirectoryInfo(dir).SetAccessControl(BuildSecurity());
                readFailed = CarryOverSettings();
            }
            else
            {
                WriteSettings(SettingsCarryOver.Rewrite(null));
            }

            if (readFailed) notes.Add("existing settings could not be read and were reset");
            return new SetupResult(true, $"Prepared {dataDir}." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return new SetupResult(false, $"Could not prepare {dataDir}: {e.Message}");
        }
    }

    public SetupResult Remove(bool purge)
    {
        if (!purge) return new SetupResult(true, $"Kept {dataDir} (use --purge to delete it).");
        try
        {
            foreach (var dir in new[] { dataDir, HistoryDir })
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new SetupResult(false, $"{dir} is a junction or symbolic link; remove it yourself. Nothing was deleted.");
            }
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            return new SetupResult(true, $"Removed {dataDir}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SetupResult(false, $"Could not remove {dataDir}: {e.Message}");
        }
    }

    /// <summary>True if something (folder, file or link, even a dangling one) is at the path.</summary>
    private static bool Existing(string path) => Directory.Exists(path) || File.Exists(path) || new DirectoryInfo(path).LinkTarget is not null;

    /// <summary>Checks an existing folder; an untrusted one is renamed aside (the link itself, for a junction). True when kept.</summary>
    private static bool EnsureFolder(string dir, List<string> notes)
    {
        var info = new DirectoryInfo(dir);
        var isReparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
        SecurityIdentifier? owner = null;
        if (!isReparse && info.Exists)
            owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (Classify(owner, isReparse) == FolderTrust.Trusted) return true;

        var aside = $"{dir}.untrusted-{DateTime.Now:yyyyMMddHHmmss}";
        if (info.Exists || isReparse) Directory.Move(dir, aside); else File.Move(dir, aside);
        notes.Add($"{dir} was not trusted (link or not owned by Administrators/SYSTEM) and was moved to {aside}; its contents were not used");
        return false;
    }

    /// <summary>Creates the folder atomically with the protected ACL and Administrators owner (no window for a user to hold rights).</summary>
    private static void CreateSecured(string dir) => new DirectoryInfo(dir).Create(BuildSecurity());

    /// <summary>
    /// Replaces any existing settings with a freshly created file: the old one may carry an attacker's ACL or content,
    /// and a new file inherits the folder's protected ACL. Returns true when the old file could not be read.
    /// </summary>
    private bool CarryOverSettings()
    {
        var path = Path.Combine(dataDir, SettingsFile);
        string? old = null;
        var readFailed = false;
        var info = new FileInfo(path);
        if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try { old = File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readFailed = true; }
        }
        WriteSettings(SettingsCarryOver.Rewrite(old));
        return readFailed;
    }

    private void WriteSettings(string json)
    {
        var path = Path.Combine(dataDir, SettingsFile);
        File.Delete(path); // no-op when missing; removes a link itself, never its target
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(new UTF8Encoding(false).GetBytes(json));
    }
}
