using System.Text.RegularExpressions;
using ProcessTracker.Core.Hooks;

namespace ProcessTracker.Core.Tests.Hooks;

public sealed class HeavyCommandClassifierTests
{
    [Theory]
    [InlineData("Bash", "cmake --build build --config Release")]
    [InlineData("PowerShell", "dotnet test --solution ProcessTracker.slnx")]
    [InlineData("Bash", "MSBuild.exe Foo.sln /p:Configuration=Release")]
    [InlineData("Bash", "make -j8")]
    [InlineData("Bash", "npm run build")]
    [InlineData("PowerShell", "python .\\scripts\\train.py --epochs 3")]
    public void IsHeavy_Builds(string tool, string command) =>
        Assert.True(HeavyCommandClassifier.IsHeavy(tool, command, []));

    [Theory]
    [InlineData("Bash", "git status")]
    [InlineData("Bash", "ls")]
    [InlineData("PowerShell", "Get-ChildItem")]
    [InlineData("Bash", "make-dir out")]
    [InlineData("Bash", null)]
    [InlineData("Read", "dotnet build")] // only Bash and PowerShell commands are classified
    public void IsHeavy_Trivial(string tool, string? command) =>
        Assert.False(HeavyCommandClassifier.IsHeavy(tool, command, []));

    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void IsHeavy_TaskTool_True(string tool) =>
        Assert.True(HeavyCommandClassifier.IsHeavy(tool, null, []));

    [Fact]
    public void IsHeavy_ExtraPattern()
    {
        Regex[] extra = [HeavyCommandClassifier.CreatePattern(@"\bbazel\s+build")];

        Assert.False(HeavyCommandClassifier.IsHeavy("Bash", "BAZEL build //...", []));
        Assert.True(HeavyCommandClassifier.IsHeavy("Bash", "BAZEL build //...", extra));
    }

    [Fact]
    public void IsHeavy_ExtraPatternTimeout_NotHeavy()
    {
        // Catastrophic backtracking on agent-supplied text: the match times out and the command counts as not heavy.
        var evil = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));

        Assert.False(HeavyCommandClassifier.IsHeavy("Bash", new string('a', 40) + "!", [evil]));
    }
}
