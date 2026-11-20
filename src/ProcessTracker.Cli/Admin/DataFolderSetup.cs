using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessTracker.Cli.Admin;

public sealed record SetupResult(bool Ok, string Message);

/// <summary>
/// Creates and secures the service's data folder (called by the installer as SYSTEM). Any user can create folders
/// under %ProgramData%, so a pre-planted junction, folder or file must not redirect or survive what SYSTEM writes here.
/// </summary>
public sealed class DataFolderSetup(string dataDirArg)
{
    private readonly string dataDir = Path.TrimEndingDirectorySeparator(dataDirArg);
    private const string SettingsFile = "appsettings.json";
    private const int MaxAttempts = 3;

    // Rights that let a principal add entries to the folder, delete it or its entries, or take it over.
    private const int Danger = 0x2 /*ADD_FILE*/ | 0x4 /*ADD_SUBDIRECTORY*/ | 0x40 /*DELETE_CHILD*/ | 0x10000 /*DELETE*/
        | 0x10 /*WRITE_EA*/ | 0x100 /*WRITE_ATTRIBUTES*/ | 0x40000 /*WRITE_DAC*/ | 0x80000 /*WRITE_OWNER*/ | 0x40000000 /*GENERIC_WRITE*/ | 0x10000000 /*GENERIC_ALL*/;

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

    /// <summary>
    /// An existing folder is kept only when it is a real directory (<paramref name="isReparse"/> also covers "not a directory"), Administrators or SYSTEM own it, and no allow ACE gives
    /// anyone else (CREATOR OWNER included) a right to add to, delete from or take over the folder. Owner alone is not
    /// enough: an Administrators-owned folder with the inherited %ProgramData% DACL still lets Users plant children.
    /// </summary>
    public static FolderTrust Classify(SecurityIdentifier? owner, bool isReparse, IReadOnlyList<AceInfo> aces)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (isReparse || owner is null || !(owner.Equals(admins) || owner.Equals(system))) return FolderTrust.Untrusted;
        return aces.Any(a => a.IsAllow && (a.Mask & Danger) != 0 && !a.Sid.Equals(system) && !a.Sid.Equals(admins))
            ? FolderTrust.Untrusted
            : FolderTrust.Trusted;
    }

    /// <summary>
    /// Creates the folder with the protected ACL and Administrators owner from birth. False when something already
    /// exists at the path (never assumed to be ours; nothing is applied to it).
    /// </summary>
    public static bool TryCreateSecured(string path) => NativeFolder.TryCreate(path, BuildSecurity().GetSecurityDescriptorBinaryForm());

    public enum BusyStep { Inspect, Lock, MoveAside }
    public enum BusyOutcome { Succeeded, Retry, ContinueUnlocked, Fail }
    public const int BusyTries = 5;
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// What to do after try <paramref name="attempt"/> (1-based) of <paramref name="step"/> ended with Win32
    /// <paramref name="error"/> (0 = success). A sharing violation only means another program has the folder open,
    /// which says nothing about trust: it is retried, then the lock is given up (the folder is already secured, which is
    /// the real protection) while inspecting or moving aside fails closed. Nothing is ever moved aside because of it.
    /// </summary>
    public static BusyOutcome Decide(BusyStep step, int error, int attempt) => error switch
    {
        0 => BusyOutcome.Succeeded,
        NativeFolder.ErrorSharingViolation when attempt < BusyTries => BusyOutcome.Retry,
        NativeFolder.ErrorSharingViolation when step == BusyStep.Lock => BusyOutcome.ContinueUnlocked,
        _ => BusyOutcome.Fail,
    };

    /// <summary>Runs <paramref name="tryOnce"/> (returns 0 or a Win32 error) as <see cref="Decide"/> says, sleeping between retries.</summary>
    public static BusyOutcome RunWhileBusy(BusyStep step, Func<int> tryOnce, out int error, Action<TimeSpan>? sleep = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            error = tryOnce();
            var outcome = Decide(step, error, attempt);
            if (outcome != BusyOutcome.Retry) return outcome;
            (sleep ?? Thread.Sleep)(BusyDelay);
        }
    }

    public SetupResult Prepare()
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            if (!IsElevated())
                return new SetupResult(false, "prepare-data must run elevated (as an administrator or SYSTEM) to set the data folder owner. Nothing was changed.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataDir))!);
            var notes = new List<string>();

            // Data folder: created secure from birth, or an existing trusted one re-secured through a handle, or an
            // untrusted one renamed aside. history\ is left to its own Acquire, so it is walked once.
            var keptExisting = Acquire(dataDir, moveAside: true, skipChild: "history", notes, handles);
            // Once the data folder is secured only administrators can add or rename entries in it, so history\ cannot be
            // raced. An untrusted history\ is therefore an administrator's doing: refuse rather than move things around inside.
            Acquire(HistoryDir, moveAside: false, skipChild: null, notes, handles);

            var readFailed = keptExisting ? CarryOverSettings() : WriteSettings(SettingsCarryOver.Rewrite(null));
            if (readFailed) notes.Add("existing settings could not be read and were reset");
            return new SetupResult(true, $"Prepared {dataDir}." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException or Win32Exception)
        {
            return new SetupResult(false, $"Could not prepare {dataDir}: {e.Message}");
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
        }
    }

    private static IOException InUse(string path) =>
        new($"{path} is in use by another program; close it and retry.");

    /// <summary>
    /// Returns true when an existing trusted folder was kept, false when a fresh one was created.
    /// Trust is decided, and the protected DACL and owner applied, through a handle without DELETE access, so other
    /// programs holding the folder open never get in the way. A trusted folder (Administrators/SYSTEM owner, nobody else
    /// may add, delete or take over) cannot be renamed, replaced or added to by users: that is the protection.
    /// A DELETE-access lock handle is then taken as a best effort and held until Prepare ends; it only adds detection of
    /// a user DELETE handle opened before the folder was secured. While any handle without share-delete is open (a
    /// terminal in the folder, say) the lock cannot be taken; after <see cref="BusyTries"/> tries Prepare goes on
    /// without it and says so. Residual risk, accepted: a handle with DELETE or WRITE_DAC that a user opened before the
    /// folder was secured stays usable (access is checked only at open).
    /// </summary>
    private static bool Acquire(string path, bool moveAside, string? skipChild, List<string> notes, List<SafeFileHandle> handles)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var created = TryCreateSecured(path);
            SafeFileHandle? handle = null;
            var opened = RunWhileBusy(BusyStep.Inspect, () => { handle = NativeFolder.OpenForSecurity(path, out var e); return e; }, out var error);
            if (opened == BusyOutcome.Fail)
            {
                if (error is NativeFolder.ErrorFileNotFound or NativeFolder.ErrorPathNotFound) continue; // vanished: retry the create
                if (error == NativeFolder.ErrorSharingViolation) throw InUse(path);
                // We cannot even read or re-secure it (a user-made DACL can lock SYSTEM out): never trusted.
                if (created || error != NativeFolder.ErrorAccessDenied) throw new IOException($"Could not open {path} (Win32 error {error}).");
            }

            using (handle)
            {
                if (handle is not null && IsTrusted(handle))
                {
                    NativeFolder.WriteSecurity(handle, BuildSecurity().GetSecurityDescriptorBinaryForm());
                    ResetChildren(new DirectoryInfo(path), skipChild);
                    TakeLock(path, handle, notes, handles);
                    return !created;
                }
            }

            if (!moveAside)
                throw new IOException($"{path} exists but is not trusted (a file or link, not owned by Administrators/SYSTEM, or writable by others); remove it and try again.");
            var aside = $"{path}.untrusted-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            if (RunWhileBusy(BusyStep.MoveAside, () => NativeFolder.TryRename(path, aside), out var renameError) != BusyOutcome.Succeeded)
            {
                if (renameError is NativeFolder.ErrorFileNotFound or NativeFolder.ErrorPathNotFound) continue; // vanished: retry the create
                if (renameError == NativeFolder.ErrorSharingViolation) throw InUse(path);
                throw new Win32Exception(renameError);
            }
            notes.Add($"{path} was not trusted (file or link, not owned by Administrators/SYSTEM, or writable by others) and was moved to {aside}; its contents were not used");
        }
        throw new IOException($"{path} kept being replaced while it was being secured ({MaxAttempts} attempts); stopped.");
    }

    private static bool IsTrusted(SafeFileHandle handle)
    {
        var (owner, aces) = NativeFolder.ReadSecurity(handle);
        return Classify(owner, !NativeFolder.IsPlainDirectory(handle), aces) == FolderTrust.Trusted;
    }

    /// <summary>Best-effort DELETE lock on the folder already secured through <paramref name="secured"/> (see <see cref="Acquire"/>).</summary>
    private static void TakeLock(string path, SafeFileHandle secured, List<string> notes, List<SafeFileHandle> handles)
    {
        SafeFileHandle? lockHandle = null;
        var outcome = RunWhileBusy(BusyStep.Lock, () => { lockHandle = NativeFolder.OpenLock(path, out var e); return e; }, out var error);
        if (outcome == BusyOutcome.ContinueUnlocked)
        {
            notes.Add($"could not take an exclusive lock on {path}; another program has the folder open");
            return;
        }
        if (outcome != BusyOutcome.Succeeded) throw new IOException($"Could not lock {path} (Win32 error {error}).");
        if (!NativeFolder.SameObject(lockHandle!, secured))
        {
            lockHandle!.Dispose();
            throw new IOException($"{path} was replaced while it was being secured; stopped.");
        }
        handles.Add(lockHandle!);
    }

    /// <summary>
    /// SetKernelObjectSecurity does not propagate: existing children keep explicit ACEs and owners. Reset each (never
    /// following links) to inherit-only with owner Administrators. The folder is secured by now, so only administrators
    /// can add or swap entries. <paramref name="skipChild"/> (top level only) is left to its own Acquire.
    /// </summary>
    private static void ResetChildren(DirectoryInfo dir, string? skipChild = null)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null) continue;
            if (skipChild is not null && string.Equals(entry.Name, skipChild, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry is DirectoryInfo sub)
            {
                var ds = new DirectorySecurity();
                ds.SetOwner(admins);
                ds.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                sub.SetAccessControl(ds);
                ResetChildren(sub);
            }
            else if (entry is FileInfo file)
            {
                var fs = new FileSecurity();
                fs.SetOwner(admins);
                fs.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                file.SetAccessControl(fs);
            }
        }
    }

    public SetupResult Remove(bool purge)
    {
        if (!purge) return new SetupResult(true, $"Kept {dataDir} (use --purge to delete it).");
        try
        {
            if (File.Exists(dataDir))
                return new SetupResult(false, $"{dataDir} is a file, not a folder; remove it yourself. Nothing was deleted.");
            foreach (var dir in new[] { dataDir, HistoryDir })
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new SetupResult(false, $"{dir} is a junction or symbolic link; remove it yourself. Nothing was deleted.");
            }
            if (Directory.Exists(dataDir))
            {
                // Never walk a tree a user could still modify: the folder must pass the same trust check as in Prepare.
                using (var handle = NativeFolder.OpenForSecurity(dataDir, out var error))
                {
                    if (handle is null)
                        return new SetupResult(false, $"Could not open {dataDir} to verify it (Win32 error {error}). Nothing was deleted.");
                    if (!IsTrusted(handle))
                        return new SetupResult(false, $"{dataDir} is not trusted (not owned by Administrators/SYSTEM, or writable by others); delete it yourself. Nothing was deleted.");
                }
                DeleteTree(new DirectoryInfo(dataDir));
            }
            return new SetupResult(true, $"Removed {dataDir}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new SetupResult(false, $"Could not remove {dataDir}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes a tree without ever following a link: link entries are removed as links. Safety against a link being
    /// swapped in mid-delete relies on the folder being trusted (only administrators can add or rename children).
    /// </summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false); else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo sub) DeleteTree(sub);
            else File.Delete(entry.FullName);
        }
        Directory.Delete(dir.FullName, recursive: false);
    }

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

    private bool WriteSettings(string json)
    {
        var path = Path.Combine(dataDir, SettingsFile);
        File.Delete(path); // no-op when missing; removes a link itself, never its target
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(new UTF8Encoding(false).GetBytes(json));
        return false;
    }
}
