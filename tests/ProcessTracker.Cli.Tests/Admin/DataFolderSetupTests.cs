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

    [Fact]
    public void Prepare_DataDirIsReparsePoint_Refused()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        MakeJunction(DataDir, target);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.False(result.Ok);
        Assert.Contains("junction", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Prepare_HistoryIsReparsePoint_Refused()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(DataDir);
        MakeJunction(Path.Combine(DataDir, "history"), target);

        var result = new DataFolderSetup(DataDir).Prepare();

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(File.Exists(Path.Combine(DataDir, "appsettings.json")));
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

        Directory.CreateDirectory(DataDir);
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
    public void Remove_WithPurge_DeletesFolder()
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "history"));
        File.WriteAllText(Path.Combine(DataDir, "state.db"), "x");

        var result = new DataFolderSetup(DataDir).Remove(purge: true);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(DataDir));
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
}
