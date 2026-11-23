using ProcessTracker.Core.Hooks;
using ProcessTracker.Core.Tests.Fakes;

namespace ProcessTracker.Core.Tests.Hooks;

public sealed class HookWarningLimiterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TakeFull_OncePer60s()
    {
        var time = new ManualTimeProvider(T0);
        var limiter = new HookWarningLimiter(time);

        Assert.True(limiter.TakeFull("a"));
        Assert.True(limiter.TakeFull("b")); // keys are independent

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TakeFull("a"));
        Assert.False(limiter.TakeFull("b"));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TakeFull("a"));
        Assert.False(limiter.TakeFull("a"));
        Assert.True(limiter.TakeFull("b"));
    }
}
