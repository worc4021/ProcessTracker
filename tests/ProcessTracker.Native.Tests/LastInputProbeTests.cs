namespace ProcessTracker.Native.Tests;

public sealed class LastInputProbeTests
{
    [Fact]
    public void Elapsed_SameEpoch() =>
        Assert.Equal(TimeSpan.FromSeconds(4), LastInputProbe.Elapsed(lastInputTick: 10_000, nowTick64: 14_000));

    [Fact]
    public void Elapsed_AcrossThe32BitWrap()
    {
        // GetLastInputInfo's tick is 32-bit and wraps every 49.7 days; TickCount64 does not.
        long now = (1L << 32) + 3_000; // 3 s after the wrap
        uint last = uint.MaxValue - 999; // 1 s before it

        Assert.Equal(TimeSpan.FromSeconds(4), LastInputProbe.Elapsed(last, now));
    }

    [Fact]
    public void SinceLastInput_DoesNotThrow()
    {
        // In an interactive session this is a small age; without a desktop it may be null. Either is fine.
        var since = new LastInputProbe().SinceLastInput();

        Assert.True(since is null || since >= TimeSpan.Zero);
    }
}
