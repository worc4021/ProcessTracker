using System.Text.Json;
using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tests.Sessions;

public sealed class SessionTextTests : IDisposable
{
    // Built from code points so that no control or bidi character appears literally in this source file.
    internal static readonly string Esc = ((char)0x1B).ToString();
    internal static readonly string Bel = ((char)0x07).ToString();
    internal static readonly string Rlo = ((char)0x202E).ToString();
    internal static readonly string Osc52 = Esc + "]52;c;ZXZpbA==" + Bel;

    internal static bool HasUnsafe(string? s) => s is not null && s.Any(SessionText.IsUnsafe);

    private readonly string _profile = Path.Combine(Path.GetTempPath(), "pt-st-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
    }

    [Theory]
    [InlineData(0x1B, true)]
    [InlineData(0x07, true)]
    [InlineData(0x7F, true)]
    [InlineData(0x9B, true)]
    [InlineData(0x202E, true)]
    [InlineData(0x2066, true)]
    [InlineData(0x200F, true)]
    [InlineData(0x061C, true)]
    [InlineData('a', false)]
    [InlineData(0x00E9, false)]
    public void IsUnsafe_Cases(int code, bool expected) => Assert.Equal(expected, SessionText.IsUnsafe((char)code));

    [Fact]
    public void Clean_DropsEscapeSequenceIntroducersAndBidi()
    {
        var cleaned = SessionText.Clean("Fix " + Osc52 + "bug" + Rlo + " now", 120);
        Assert.False(HasUnsafe(cleaned));
        Assert.Equal("Fix ]52;c;ZXZpbA== bug now", cleaned);
    }

    [Fact]
    public void TranscriptTitleAndCwd_AreCleanedAtExtraction()
    {
        var dir = Path.Combine(_profile, ".claude", "projects", "C--x");
        Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "user",
            ["cwd"] = @"C:\repos\Evil" + Osc52 + Rlo,
            ["message"] = new Dictionary<string, object> { ["content"] = "Fix " + Osc52 + "bug" + Rlo + " now" },
        });
        File.WriteAllText(Path.Combine(dir, "hostile.jsonl"), line + "\n");

        var info = new TranscriptReader(_profile).Read("hostile")!;
        Assert.False(HasUnsafe(info.Title));
        Assert.False(HasUnsafe(info.Cwd));
        Assert.Equal("Fix ]52;c;ZXZpbA== bug now", info.Title);
        Assert.Equal(@"C:\repos\Evil ]52;c;ZXZpbA==", info.Cwd);
    }

    [Fact]
    public void ReaderSources_HaveNoLiteralBidiOrZeroWidthCharacters()
    {
        // Trojan Source guard: such characters must be written as \u escapes in source.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ProcessTracker.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var offenders = new[] { "src", "tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && (f.EndsWith(".cs") || f.EndsWith(".js") || f.EndsWith(".css") || f.EndsWith(".html")))
            .Where(f => File.ReadAllText(f).Any(c => SessionText.IsBidiControl(c) || c is >= (char)0x200B and <= (char)0x200D))
            .ToList();
        Assert.Empty(offenders);
    }
}
