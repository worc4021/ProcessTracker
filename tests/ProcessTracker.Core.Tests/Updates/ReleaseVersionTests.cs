using ProcessTracker.Core.Updates;

namespace ProcessTracker.Core.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.3.0-dev.202611231200", "0.3.0")]
    [InlineData("1.10.3+abc", "1.10.3")]
    [InlineData("garbage", null)]
    [InlineData("not-a-version", null)]
    [InlineData("1.2", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("", null)]
    public void Numeric_TakesXyz(string informational, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), ReleaseVersion.Numeric(informational));
}
