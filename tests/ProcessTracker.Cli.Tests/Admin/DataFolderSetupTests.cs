using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public sealed class DataFolderSetupTests : IDisposable
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    // Always a temp dir: these tests never touch the real %ProgramData%.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proctrack-admin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).ToList())
                if (Directory.Exists(d) && new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d); // junction only
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* temp leftovers are harmless */ }
    }

    private string DataDir => Path.Combine(_root, "ProcessTracker");

    private static void MakeJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void BuildSecurity_HasProtectedAclWithExpectedRules()
    {
        var sec = DataFolderSetup.BuildSecurity();
        Assert.True(sec.AreAccessRulesProtected);
        Assert.Equal(Admins, sec.GetOwner(typeof(SecurityIdentifier)));

        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var r in rules)
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(inherit, r.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, r.PropagationFlags);
        }
        Assert.Equal(FileSystemRights.FullControl, rules.Single(r => r.IdentityReference == SystemSid).FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, rules.Single(r => r.IdentityReference == Admins).FileSystemRights);
        // Windows reads back ReadAndExecute with Synchronize added; what matters is that no write right is present.
        var userRights = rules.Single(r => r.IdentityReference == Users).FileSystemRights;
        Assert.Equal(FileSystemRights.ReadAndExecute, userRights & ~FileSystemRights.Synchronize);
    }

    private const int ReadExec = 0x1200A9, FullControlMask = 0x1F01FF, AddFile = 0x2;

    private static AceInfo Allow(SecurityIdentifier sid, int mask) => new(sid, mask, true);

    private static DataFolderSetup.FolderTrust Classify(SecurityIdentifier? owner, bool reparse, params AceInfo[] aces) =>
        DataFolderSetup.Classify(owner, reparse, aces);

    /// <summary>Elevated only: a folder created with our protected Admin/SYSTEM DACL, i.e. one Prepare must keep.</summary>
    private void CreateTrustedDataDir()
    {
        Directory.CreateDirectory(_root);
        Assert.True(DataFolderSetup.TryCreateSecured(DataDir));
    }

    [Fact]
    public void Classify_Phase1InstallAcl_IsTrusted()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(DataFolderSetup.FolderTrust.Trusted,
            Classify(Admins, false, Allow(SystemSid, FullControlMask), Allow(Admins, FullControlMask), Allow(me, ReadExec)));
    }

    [Fact]
    public void Classify_OurOwnAcl_IsTrusted_ForAdminsAndSystemOwners()
    {
        AceInfo[] aces = [Allow(SystemSid, FullControlMask), Allow(Admins, FullControlMask), Allow(Users, ReadExec)];
        Assert.Equal(DataFolderSetup.FolderTrust.Trusted, DataFolderSetup.Classify(Admins, false, aces));
        Assert.Equal(DataFolderSetup.FolderTrust.Trusted, DataFolderSetup.Classify(SystemSid, false, aces));
    }

    [Fact]
    public void Classify_WrongOwnerLinkOrNoOwner_IsUntrusted()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var safe = Allow(Admins, FullControlMask);
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(me, false, safe));
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(Users, false, safe));
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(null, false, safe));
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(Admins, true, safe));
    }

    [Theory]
    [InlineData(0x2)]        // FILE_ADD_FILE / WRITE_DATA
    [InlineData(0x4)]        // FILE_ADD_SUBDIRECTORY / APPEND_DATA
    [InlineData(0x10)]       // FILE_WRITE_EA
    [InlineData(0x40)]       // FILE_DELETE_CHILD
    [InlineData(0x100)]      // FILE_WRITE_ATTRIBUTES
    [InlineData(0x10000)]    // DELETE
    [InlineData(0x40000)]    // WRITE_DAC
    [InlineData(0x80000)]    // WRITE_OWNER
    [InlineData(0x40000000)] // GENERIC_WRITE
    [InlineData(0x10000000)] // GENERIC_ALL
    public void Classify_AdminOwnedButOthersCanWrite_IsUntrusted(int mask)
    {
        var creatorOwner = new SecurityIdentifier("S-1-3-0");
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(Admins, false, Allow(Admins, FullControlMask), Allow(Users, mask)));
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, Classify(Admins, false, Allow(Admins, FullControlMask), Allow(creatorOwner, mask)));
    }

    [Fact]
    public void Classify_DenyAcesAndReadOnlyRights_DoNotMakeUntrusted()
    {
        Assert.Equal(DataFolderSetup.FolderTrust.Trusted,
            Classify(Admins, false, Allow(Admins, FullControlMask), new AceInfo(Users, AddFile, false), Allow(Users, ReadExec)));
    }

    [Fact]
    public void TryCreateSecured_ExistingPath_ReportsExisting_AndChangesNothing()
    {
        Directory.CreateDirectory(DataDir);
        var before = new DirectoryInfo(DataDir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);

        Assert.False(DataFolderSetup.TryCreateSecured(DataDir));

        var after = new DirectoryInfo(DataDir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Prepare_AdminOwnedFolderWithInheritedUsersAddFile_IsMovedAside()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Needs an elevated process.");

        // Otherwise-trusted folder (Administrators owner, protected Admin/SYSTEM DACL) plus ONLY a Users AddFile ACE.
        CreateTrustedDataDir();
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "planted");
        var acl = new DirectoryInfo(DataDir).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateFiles | FileSystemRights.AppendData,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DataDir).SetAccessControl(acl);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.True(result.Ok, result.Message);
        Assert.Contains("untrusted", result.Message, StringComparison.OrdinalIgnoreCase);
        var aside = Assert.Single(Directory.GetDirectories(_root, "ProcessTracker.untrusted-*"));
        Assert.True(File.Exists(Path.Combine(aside, "state.db")));
        Assert.False(File.Exists(Path.Combine(DataDir, "state.db")));
    }

    [Fact]
    public void Prepare_DataDirIsJunction_NeverTouchesTarget_AndMovesLinkAsideWhenElevated()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        MakeJunction(DataDir, target);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
        if (!DataFolderSetup.IsElevated())
        {
            Assert.False(result.Ok);
            return;
        }
        Assert.True(result.Ok, result.Message);
        Assert.Contains("untrusted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(new DirectoryInfo(DataDir).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Single(Directory.GetDirectories(_root, "ProcessTracker.untrusted-*"));
    }

    [Fact]
    public void Prepare_UserOwnedFolder_IsMovedAside_AndFreshFolderCreated()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Needs an elevated process.");

        var me = WindowsIdentity.GetCurrent().User!;
        Directory.CreateDirectory(Path.Combine(DataDir, "history"));
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "planted");
        File.WriteAllText(Path.Combine(DataDir, "appsettings.json"), """{"RetentionHours":48}""");
        var planted = new DirectoryInfo(DataDir).GetAccessControl();
        planted.SetOwner(me);
        planted.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.FullControl, AccessControlType.Allow));
        new DirectoryInfo(DataDir).SetAccessControl(planted);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.True(result.Ok, result.Message);
        Assert.Contains("untrusted", result.Message, StringComparison.OrdinalIgnoreCase);
        var aside = Assert.Single(Directory.GetDirectories(_root, "ProcessTracker.untrusted-*"));
        Assert.True(File.Exists(Path.Combine(aside, "state.db")));
        Assert.False(File.Exists(Path.Combine(DataDir, "state.db")));
        Assert.DoesNotContain("48", File.ReadAllText(Path.Combine(DataDir, "appsettings.json")));
        var fresh = new DirectoryInfo(DataDir).GetAccessControl();
        Assert.Equal(Admins, fresh.GetOwner(typeof(SecurityIdentifier)));
        Assert.DoesNotContain(fresh.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference == Everyone);
        Assert.True(Directory.Exists(Path.Combine(DataDir, "history")));
    }

    [Fact]
    public void Remove_WithPurge_NestedJunction_TargetKept()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Needs a trusted (Administrators-owned) folder, which needs elevation.");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        CreateTrustedDataDir();
        MakeJunction(Path.Combine(DataDir, "nested"), target);

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(DataDir));

        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Prepare_NotElevated_RefusesAndChangesNothing()
    {
        Assert.SkipWhen(DataFolderSetup.IsElevated(), "Needs a non-elevated process.");

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.False(result.Ok);
        Assert.Contains("elevat", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(DataDir));
    }

    [Fact]
    public void Prepare_PlantedSettingsFile_IsReplacedWithFreshFile()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Setting the Administrators owner needs an elevated process.");

        CreateTrustedDataDir(); // a folder under %TEMP% would inherit the current user's ACE and be untrusted
        var settings = Path.Combine(DataDir, "appsettings.json");
        File.WriteAllText(settings, """{"RetentionHours":48,"DataDir":"D:\\evil"}""");
        var planted = new FileInfo(settings).GetAccessControl();
        planted.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(settings).SetAccessControl(planted);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.True(result.Ok, result.Message);
        var fresh = new FileInfo(settings).GetAccessControl();
        Assert.DoesNotContain(fresh.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference == Everyone);
        Assert.Contains("48", File.ReadAllText(settings));
        Assert.DoesNotContain("evil", File.ReadAllText(settings));

        var dir = new DirectoryInfo(DataDir).GetAccessControl();
        Assert.True(dir.AreAccessRulesProtected);
        Assert.Equal(Admins, dir.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(Directory.Exists(Path.Combine(DataDir, "history")));
    }

    [Fact]
    public void Remove_WithoutPurge_KeepsFolder()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "x");

        var result = new DataFolderSetup(DataDir).Remove(purge: false);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(DataDir, "state.db")));
    }

    [Fact]
    public void Remove_WithPurge_DeletesTrustedFolder()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Needs a trusted (Administrators-owned) folder, which needs elevation.");
        CreateTrustedDataDir();
        Directory.CreateDirectory(Path.Combine(DataDir, "history"));
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "x");

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(DataDir));
    }

    [Fact]
    public void Remove_WithPurge_UntrustedFolder_RefusedAndKept()
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "history")); // under %TEMP%: inherits the current user's rights
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "x");

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(DataDir, "state.db")));
    }

    [Fact]
    public void Remove_WithPurge_ReparsePoint_RefusedAndTargetKept()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        MakeJunction(DataDir, target);

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Remove_MissingFolder_IsOk()
    {
        Assert.True(new DataFolderSetup(DataDir).Remove(purge: true).Ok);
    }

    [Fact]
    public void Remove_WithPurge_PathIsAFile_RefusedAndKept()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(DataDir, "x");

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.False(result.Ok);
        Assert.Contains("not a folder", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(DataDir));
    }

    [Fact]
    public void Prepare_TrustedFolder_ResetsChildAcls_AndDoesNotFollowChildJunction()
    {
        Assert.SkipUnless(DataFolderSetup.IsElevated(), "Needs a trusted (Administrators-owned) folder, which needs elevation.");

        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        CreateTrustedDataDir();
        var targetBefore = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        MakeJunction(Path.Combine(DataDir, "linked"), target);

        var me = WindowsIdentity.GetCurrent().User!;
        var child = Path.Combine(DataDir, "state.db");
        File.WriteAllText(child, "x");
        var planted = new FileInfo(child).GetAccessControl();
        planted.SetOwner(me);
        planted.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(child).SetAccessControl(planted);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.True(result.Ok, result.Message);
        var reset = new FileInfo(child).GetAccessControl();
        Assert.Equal(Admins, reset.GetOwner(typeof(SecurityIdentifier)));
        Assert.DoesNotContain(reset.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference == Everyone);
        var targetAfter = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.Equal(targetBefore, targetAfter);
    }

    [Theory]
    [InlineData(DataFolderSetup.BusyStep.Inspect)]
    [InlineData(DataFolderSetup.BusyStep.Lock)]
    [InlineData(DataFolderSetup.BusyStep.MoveAside)]
    public void Decide_SharingViolation_RetriesBeforeTheLastTry(DataFolderSetup.BusyStep step)
    {
        for (var attempt = 1; attempt < DataFolderSetup.BusyTries; attempt++)
            Assert.Equal(DataFolderSetup.BusyOutcome.Retry, DataFolderSetup.Decide(step, ErrorSharingViolation, attempt));
    }

    [Fact]
    public void Decide_SharingViolationOnLastTry_LockContinuesUnlocked_OthersFail()
    {
        Assert.Equal(5, DataFolderSetup.BusyTries);
        Assert.Equal(DataFolderSetup.BusyOutcome.ContinueUnlocked, DataFolderSetup.Decide(DataFolderSetup.BusyStep.Lock, ErrorSharingViolation, 5));
        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, DataFolderSetup.Decide(DataFolderSetup.BusyStep.MoveAside, ErrorSharingViolation, 5));
        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, DataFolderSetup.Decide(DataFolderSetup.BusyStep.Inspect, ErrorSharingViolation, 5));
    }

    [Theory]
    [InlineData(DataFolderSetup.BusyStep.Inspect)]
    [InlineData(DataFolderSetup.BusyStep.Lock)]
    [InlineData(DataFolderSetup.BusyStep.MoveAside)]
    public void Decide_SuccessAndOtherErrors_AreNotRetried(DataFolderSetup.BusyStep step)
    {
        Assert.Equal(DataFolderSetup.BusyOutcome.Succeeded, DataFolderSetup.Decide(step, 0, 1));
        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, DataFolderSetup.Decide(step, 5 /*ACCESS_DENIED*/, 1));
        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, DataFolderSetup.Decide(step, 2 /*FILE_NOT_FOUND*/, 1));
    }

    private const int ErrorSharingViolation = 32;

    [Fact]
    public void RunWhileBusy_LockAlwaysBusy_TriesFiveTimes200MsApart_ThenContinuesUnlocked()
    {
        var tries = 0;
        var sleeps = new List<TimeSpan>();

        var outcome = DataFolderSetup.RunWhileBusy(DataFolderSetup.BusyStep.Lock, () => { tries++; return ErrorSharingViolation; }, out var error, sleeps.Add);

        Assert.Equal(DataFolderSetup.BusyOutcome.ContinueUnlocked, outcome);
        Assert.Equal(ErrorSharingViolation, error);
        Assert.Equal(5, tries);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(200), 4), sleeps);
    }

    [Fact]
    public void RunWhileBusy_MoveAsideAlwaysBusy_Fails()
    {
        var tries = 0;
        var outcome = DataFolderSetup.RunWhileBusy(DataFolderSetup.BusyStep.MoveAside, () => { tries++; return ErrorSharingViolation; }, out _, _ => { });

        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, outcome);
        Assert.Equal(5, tries);
    }

    [Fact]
    public void RunWhileBusy_BusyThenFree_Succeeds()
    {
        var tries = 0;
        var outcome = DataFolderSetup.RunWhileBusy(DataFolderSetup.BusyStep.Lock, () => ++tries < 3 ? ErrorSharingViolation : 0, out var error, _ => { });

        Assert.Equal(DataFolderSetup.BusyOutcome.Succeeded, outcome);
        Assert.Equal(0, error);
        Assert.Equal(3, tries);
    }

    [Fact]
    public void RunWhileBusy_OtherError_StopsAtOnce()
    {
        var tries = 0;
        var outcome = DataFolderSetup.RunWhileBusy(DataFolderSetup.BusyStep.Lock, () => { tries++; return 5; }, out var error, _ => { });

        Assert.Equal(DataFolderSetup.BusyOutcome.Fail, outcome);
        Assert.Equal(5, error);
        Assert.Equal(1, tries);
    }
}
