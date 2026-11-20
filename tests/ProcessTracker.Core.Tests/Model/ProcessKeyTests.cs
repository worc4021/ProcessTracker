using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Tests.Model;

public class ProcessKeyTests
{
    [Fact]
    public void SamePidDifferentCreateTime_AreDifferentProcesses()
        => Assert.NotEqual(new ProcessKey(42, 1000), new ProcessKey(42, 2000));

    [Fact]
    public void RoundTripsThroughString()
    {
        var k = new ProcessKey(42, 133_000_000_000_000_000);
        Assert.True(ProcessKey.TryParse(k.ToString(), out var parsed));
        Assert.Equal(k, parsed);
    }

    [Fact]
    public void TryParse_Garbage_False() => Assert.False(ProcessKey.TryParse("42", out _));
}
