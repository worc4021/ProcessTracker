using System.Security.Principal;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public class InstallDirCheckTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pt-installdir-" + Guid.NewGuid().ToString("N"));

    public InstallDirCheckTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Theory]
    [InlineData(@"C:\Program Files\ProcessTracker", @"C:\Program Files\ProcessTracker")]
    [InlineData(@"C:\Program Files\ProcessTracker""",@"C:\Program Files\ProcessTracker")]
    [InlineData(@"C:\Program Files\ProcessTracker\", @"C:\Program Files\ProcessTracker")]
    public void Normalize_TrimsTrailingQuoteAndSeparator(string input, string expected) =>
        Assert.Equal(expected, InstallDirCheck.Normalize(input));

    [Fact]
    public void Validate_RelativePath_IsRefused() =>
        Assert.Contains("fully qualified", InstallDirCheck.Validate(@"ProcessTracker", _ => true));

    [Fact]
    public void Validate_MissingScript_IsRefused() =>
        Assert.Contains("upgrade.ps1 does not exist", InstallDirCheck.Validate(dir, _ => true));

    [Fact]
    public void Validate_NotAdminOnly_IsRefused()
    {
        File.WriteAllText(Path.Combine(dir, "upgrade.ps1"), "");
        Assert.Contains("not admin-only", InstallDirCheck.Validate(dir, _ => false));
    }

    [Fact]
    public void Validate_AllChecksPass_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(dir, "upgrade.ps1"), "");
        Assert.Null(InstallDirCheck.Validate(dir, _ => true));
    }

    [Fact]
    public void IsAdminOnly_UserTempFolder_IsFalse() => Assert.False(InstallDirCheck.IsAdminOnly(dir));

    [Fact]
    public void IsAdminOnly_MissingFolder_IsFalse() => Assert.False(InstallDirCheck.IsAdminOnly(Path.Combine(dir, "nope")));

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private const int GenericAll = 0x10000000, Modify = 0x1301BF;

    [Fact]
    public void Classify_InheritOnlyCreatorOwner_IsTrusted() =>
        Assert.Equal(DataFolderSetup.FolderTrust.Trusted, DataFolderSetup.Classify(Admins, false,
            [new AceInfo(Admins, GenericAll, true), new AceInfo(CreatorOwner, GenericAll, true, InheritOnly: true)]));

    [Fact]
    public void Classify_NonInheritOnlyCreatorOwner_IsUntrusted() =>
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, DataFolderSetup.Classify(Admins, false,
            [new AceInfo(Admins, GenericAll, true), new AceInfo(CreatorOwner, GenericAll, true)]));

    [Fact]
    public void Classify_InheritOnlyUsersWrite_IsUntrusted() =>
        Assert.Equal(DataFolderSetup.FolderTrust.Untrusted, DataFolderSetup.Classify(Admins, false,
            [new AceInfo(Admins, GenericAll, true), new AceInfo(Users, Modify, true, InheritOnly: true)]));

    [Fact]
    public void IsAdminOnly_RealProgramFilesDotnet_IsTrusted()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        Assert.SkipUnless(Directory.Exists(dotnet), "Program Files dotnet folder is not present.");
        Assert.True(InstallDirCheck.IsAdminOnly(dotnet));
    }
}
