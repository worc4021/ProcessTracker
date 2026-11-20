namespace ProcessTracker.Core.Tests.Fakes;

/// <summary>A TimeProvider whose clock only moves when the test says so.</summary>
public sealed class ManualTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
