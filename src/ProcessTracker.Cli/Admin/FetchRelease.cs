using System.Reflection;
using ProcessTracker.Core.Updates;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// proctrack admin fetch-release: downloads the latest GitHub release's MSI, hash-verified by
/// <see cref="ReleaseClient"/>, when it is strictly newer than <paramref name="installed"/>. upgrade.ps1 runs it as
/// SYSTEM when winget cannot upgrade, then installs the MSI itself (msiexec replaces this exe, so it must have exited).
/// Prints only the verified path on stdout; everything else goes to stderr.
/// </summary>
public sealed class FetchRelease(ReleaseClient releases, Version installed, TextWriter? output = null, TextWriter? error = null)
{
    public const int Downloaded = 0, Failed = 1, UpToDate = 3;

    private readonly TextWriter _out = output ?? Console.Out;
    private readonly TextWriter _err = error ?? Console.Error;

    /// <summary>The numeric X.Y.Z of this proctrack build (what the MSI installed), or null for an unparseable version.</summary>
    public static Version? InstalledVersion() =>
        NumericVersion(typeof(FetchRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    /// <summary>X.Y.Z of an informational version such as <c>0.3.0</c>, <c>0.3.0-dev.stamp</c> or <c>0.3.0+hash</c>.</summary>
    public static Version? NumericVersion(string informational)
    {
        var core = informational.Split('+', '-')[0];
        return Version.TryParse(core, out var v) && v.Build >= 0 && v.Revision < 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    /// <summary>The release asset name for <paramref name="asset"/> (only <c>msi</c>) at <paramref name="version"/>.</summary>
    public static string AssetName(string asset, Version version) => asset switch
    {
        "msi" => $"ProcessTracker-{version.Major}.{version.Minor}.{version.Build}-x64.msi",
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, "Only msi is supported."),
    };

    /// <returns><see cref="Downloaded"/>, <see cref="UpToDate"/> or <see cref="Failed"/>.</returns>
    public async Task<int> RunAsync(string asset, string outDir, CancellationToken ct)
    {
        try
        {
            outDir = Path.GetFullPath(outDir);
            var latest = await releases.GetLatestAsync(ct);
            if (latest is null)
            {
                await _err.WriteLineAsync("Could not read the latest ProcessTracker release from GitHub.");
                return Failed;
            }
            if (!ReleaseClient.IsNewer(latest.Version, installed))
            {
                await _err.WriteLineAsync($"ProcessTracker {installed} is up to date (latest release {latest.Version}).");
                return UpToDate;
            }

            var name = AssetName(asset, latest.Version);
            var path = await releases.DownloadVerifiedAsync(latest, name, outDir, ct);
            if (path is null)
            {
                await _err.WriteLineAsync($"Could not download and verify {name} from release {latest.Tag}.");
                return Failed;
            }
            await _out.WriteLineAsync(path);
            return Downloaded;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException
                                      or UnauthorizedAccessException or OperationCanceledException)
        {
            await _err.WriteLineAsync($"fetch-release failed: {e.Message}");
            return Failed;
        }
    }
}
