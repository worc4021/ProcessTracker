namespace ProcessTracker.Cli.Tests;

public class FormatTests
{
    // Built from code points so that no control or bidi character appears literally in this source file.
    private static readonly string Esc = ((char)0x1B).ToString();
    private static readonly string Bel = ((char)0x07).ToString();
    private static readonly string Rlo = ((char)0x202E).ToString();
    private static readonly string Csi = ((char)0x9B).ToString(); // C1 control introducer
    private static readonly string Osc52 = Esc + "]52;c;ZXZpbA==" + Bel;

    private static bool HasUnsafe(string s) =>
        s.Any(c => char.IsControl(c) && c is not '\r' and not '\n' || c is >= (char)0x202A and <= (char)0x202E
                   || c is >= (char)0x2066 and <= (char)0x2069 || c is (char)0x200E or (char)0x200F);

    [Fact]
    public void Truncate_RemovesTerminalEscapesAndBidiControls()
    {
        var hostile = "Fix " + Osc52 + "bug" + Rlo + " now" + Csi + "2J";
        var cell = Format.Truncate(hostile, 80);
        Assert.False(HasUnsafe(cell));
        Assert.StartsWith("Fix ?]52;c;ZXZpbA==?bug? now?2J", cell);
    }

    [Fact]
    public void Safe_LineBreaksAndTabsBecomeSpaces()
    {
        Assert.Equal("a b c", Format.Safe("a\r\nb\tc"));
        Assert.Equal("", Format.Safe(null));
        Assert.Equal("plain", Format.Safe("plain"));
    }

    [Fact]
    public void Table_SanitizesEveryCell_EvenWithoutTruncate()
    {
        var table = Format.Table(["SESSION", "UNIT"], [["t" + Osc52, "u" + Rlo + "x"]]);
        Assert.False(HasUnsafe(table));
        Assert.Contains("t?]52;c;ZXZpbA==?", table);
        Assert.Contains("u?x", table);
    }
}
