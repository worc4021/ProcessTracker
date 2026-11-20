using ProcessTracker.Core.Model;
using ProcessTracker.Core.Storage;

namespace ProcessTracker.Core.Tests.Storage;

public class SampleRingTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TickFrame Frame(int i) => new(
        T0.AddSeconds(2 * i), new SystemSample(T0.AddSeconds(2 * i), 0, 0, 1, 0, 0, 0, 0),
        Array.Empty<ProcessSample>(), Array.Empty<SessionAggregate>());

    [Fact]
    public void Ring_KeepsLast300_InOrder()
    {
        var ring = new SampleRing(300);
        for (var i = 0; i < 450; i++) ring.Add(Frame(i));

        var all = ring.Since(DateTime.MinValue);
        Assert.Equal(300, all.Count);
        Assert.Equal(T0.AddSeconds(2 * 150), all[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(2 * 449), all[^1].TimestampUtc);
        Assert.Equal(all[^1], ring.Latest);
        Assert.All(all.Zip(all.Skip(1)), p => Assert.True(p.First.TimestampUtc < p.Second.TimestampUtc));
    }

    [Fact]
    public void Ring_Since_FiltersByTime()
    {
        var ring = new SampleRing(300);
        Assert.Null(ring.Latest);
        for (var i = 0; i < 10; i++) ring.Add(Frame(i));

        var since = ring.Since(T0.AddSeconds(2 * 7));
        Assert.Equal(3, since.Count);
        Assert.Equal(T0.AddSeconds(14), since[0].TimestampUtc);
    }
}
