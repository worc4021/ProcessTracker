using ProcessTracker.Cli;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Cli.Tests;

public class CallerIdentityTests
{
    [Fact]
    public void HostEnvPresent_ServiceNull_SessionOfHost()
    {
        var c = CallerIdentity.Resolve(null, "local_abc", "sid1");
        Assert.Equal(AttributionKind.Session, c!.Kind);
        Assert.Equal("host:local_abc", c.UnitId);
    }

    [Fact]
    public void HostEnvPresent_OverridesServiceAnswer()
    {
        var other = new Attribution(AttributionKind.Unattributed, null, null, false);
        Assert.Equal("host:local_abc", CallerIdentity.Resolve(other, "local_abc", null)!.UnitId);
    }

    [Fact]
    public void SessionEnvOnly_ServiceNull_FailsClosed()
    {
        var c = CallerIdentity.Resolve(null, null, "sid1");
        Assert.Equal(AttributionKind.Session, c!.Kind);
        Assert.Null(c.UnitId);
    }

    [Fact]
    public void SessionEnvOnly_ServiceSession_UsesItsUnit()
    {
        var svc = new Attribution(AttributionKind.Session, "sid:xyz", "sid1", false);
        Assert.Equal("sid:xyz", CallerIdentity.Resolve(svc, null, "sid1")!.UnitId);
    }

    [Fact]
    public void NoEnv_UsesServiceAnswer()
    {
        Assert.Null(CallerIdentity.Resolve(null, "", null));
        Assert.Equal(AttributionKind.ClaudeApp,
            CallerIdentity.Resolve(new Attribution(AttributionKind.ClaudeApp, null, null, false), null, "")!.Kind);
    }
}
