using System.Reflection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests;

public class VersionTests
{
    [Fact]
    public void AssemblyVersion_MatchesVersionTxt()
    {
        var expected = File.ReadAllText(Path.Combine(RepoRoot(), "version.txt")).Trim();
        var info = typeof(ProcessKey).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(expected, info);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "version.txt"))) dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("version.txt not found above " + AppContext.BaseDirectory);
    }
}
