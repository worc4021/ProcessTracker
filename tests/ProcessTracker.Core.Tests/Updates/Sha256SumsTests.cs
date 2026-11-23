using ProcessTracker.Core.Updates;

namespace ProcessTracker.Core.Tests.Updates;

public class Sha256SumsTests
{
    private const string HexA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HexB = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Sha256Sums_ParsesTwoSpaceFormat()
    {
        var text = $"{HexA}  ProcessTracker-0.3.0-x64.msi\n\n{HexB.ToUpperInvariant()}  ProcessTracker-0.3.0-x64.zip\r\n";
        var sums = Sha256Sums.Parse(text);
        Assert.Equal(2, sums.Count);
        Assert.Equal(HexA, sums["ProcessTracker-0.3.0-x64.msi"]);
        Assert.Equal(HexB, sums["ProcessTracker-0.3.0-x64.zip"]);
    }

    [Theory]
    [InlineData("not a sums line")]
    [InlineData("abc123  file.zip")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa file.zip")]
    public void Sha256Sums_MalformedLine_Throws(string line)
    {
        Assert.Throws<FormatException>(() => Sha256Sums.Parse(line));
    }
}
