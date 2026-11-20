using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public class WingetLocatorTests
{
    private const string Root = @"C:\Program Files\WindowsApps\";

    [Fact]
    public void WingetLocator_PicksHighestVersion()
    {
        var picked = WingetLocator.Pick([
            Root + "Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe",
            Root + "Microsoft.DesktopAppInstaller_1.10.320.0_x64__8wekyb3d8bbwe"]);
        Assert.Equal(Root + @"Microsoft.DesktopAppInstaller_1.10.320.0_x64__8wekyb3d8bbwe\winget.exe", picked);
    }

    [Fact]
    public void WingetLocator_IgnoresOtherArchesAndJunk()
    {
        var picked = WingetLocator.Pick([
            Root + "Microsoft.DesktopAppInstaller_9.0.0.0_arm64__8wekyb3d8bbwe",
            Root + "Microsoft.DesktopAppInstaller_9.0.0.0_neutral_~_8wekyb3d8bbwe",
            Root + "Microsoft.DesktopAppInstaller_x.y_x64__8wekyb3d8bbwe",
            Root + "Other.App_99.0.0.0_x64__8wekyb3d8bbwe",
            Root + "Microsoft.DesktopAppInstaller_1.2.3.4_x64__8wekyb3d8bbwe"]);
        Assert.Equal(Root + @"Microsoft.DesktopAppInstaller_1.2.3.4_x64__8wekyb3d8bbwe\winget.exe", picked);
    }

    [Fact]
    public void WingetLocator_None_ReturnsNull()
    {
        Assert.Null(WingetLocator.Pick([]));
        Assert.Null(WingetLocator.Pick([Root + "Junk"]));
    }
}
