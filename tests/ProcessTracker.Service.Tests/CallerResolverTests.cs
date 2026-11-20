using System.Collections.Frozen;
using System.Net;
using Microsoft.AspNetCore.Http;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Tracking;
using ProcessTracker.Service.Tests.Fakes;

namespace ProcessTracker.Service.Tests;

public sealed class CallerResolverTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const int ClientPort = 50123, ServerPort = 47812;

    private readonly FakeTrackerHost _host = new();

    private static ProcessKey Key(int pid) => new(pid, 1_000_000 + pid);

    private static ProcessView Proc(ProcessKey key, Attribution attribution)
    {
        var record = new ProcessRecord(key, null, "claude.exe", null, null, attribution, T0, null);
        return new ProcessView(record, new ProcessSample(key, T0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), default, null, default, false);
    }

    private void SetProcesses(params ProcessView[] processes) =>
        _host.View = new TrackerView(T0, default, processes.ToFrozenDictionary(p => p.Record.Key), []);

    private static DefaultHttpContext Request(string? sessionHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Connection.RemotePort = ClientPort;
        ctx.Connection.LocalIpAddress = IPAddress.Loopback;
        ctx.Connection.LocalPort = ServerPort;
        if (sessionHeader is not null) ctx.Request.Headers["X-Claude-Session"] = sessionHeader;
        return ctx;
    }

    /// <summary>A resolver whose TCP table says the request's client socket is owned by <paramref name="owner"/>.</summary>
    private CallerResolver Resolver(ProcessKey? owner) =>
        new(_host, c => c.RemotePort == ClientPort && c.LocalPort == ServerPort
                        && Equals(c.RemoteIpAddress, IPAddress.Loopback) && Equals(c.LocalIpAddress, IPAddress.Loopback)
            ? owner : null);

    [Fact]
    public void Resolver_TcpOwnerIsSessionProcess_ReturnsIt()
    {
        var session = new Attribution(AttributionKind.Session, "host:local_a", "cc-1", true);
        SetProcesses(Proc(Key(42), session), Proc(Key(43), Attribution.Unattributed));

        // The header is ignored when the connection already resolves to a session.
        var caller = Resolver(Key(42)).Resolve(Request(sessionHeader: "local_other"));

        Assert.Equal(session, caller);
    }

    [Fact]
    public void Resolver_TcpOwnerIsClaudeApp_FallsBackToHeader()
    {
        SetProcesses(Proc(Key(42), new Attribution(AttributionKind.ClaudeApp, null, null, false)));

        var caller = Resolver(Key(42)).Resolve(Request(sessionHeader: "local_b"));

        Assert.Equal(new Attribution(AttributionKind.Session, "host:local_b", null, false), caller);
    }

    [Fact]
    public void Resolver_NoMatch_NoHeader_Null()
    {
        SetProcesses(Proc(Key(42), Attribution.Unattributed));

        Assert.Null(Resolver(owner: null).Resolve(Request()));
    }

    [Fact]
    public void Resolver_TcpOwnerUnattributed_NoHeader_ReturnsUnattributed()
    {
        SetProcesses(Proc(Key(42), Attribution.Unattributed));

        Assert.Equal(Attribution.Unattributed, Resolver(Key(42)).Resolve(Request()));
    }

    [Fact]
    public void Resolver_SamePidDifferentCreateTime_IsUnresolved()
    {
        // The view (up to one tick old) still holds an exited session process whose PID the caller now reuses.
        SetProcesses(Proc(Key(42), new Attribution(AttributionKind.Session, "host:local_old", null, false)));
        var reused = new ProcessKey(42, Key(42).CreateTimeTicks + 1);

        Assert.Null(Resolver(reused).Resolve(Request()));
        Assert.Equal(new Attribution(AttributionKind.Session, "host:local_b", null, false),
            Resolver(reused).Resolve(Request(sessionHeader: "local_b")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("local a")]
    [InlineData("local;a")]
    [InlineData("${CLAUDE_CODE_HOST_SESSION_ID}")]
    [InlineData("local_é")]
    public void Resolver_MalformedHeader_IsIgnored(string header)
    {
        SetProcesses();

        Assert.Null(Resolver(owner: null).Resolve(Request(sessionHeader: header)));
    }

    [Fact]
    public void Resolver_HeaderLength_IsBounded()
    {
        SetProcesses();

        Assert.Equal("host:" + new string('a', 128), Resolver(owner: null).Resolve(Request(new string('a', 128)))?.UnitId);
        Assert.Null(Resolver(owner: null).Resolve(Request(new string('a', 129))));
        Assert.Equal("host:local_f34c6d63-6348.4e50:x", Resolver(owner: null).Resolve(Request("local_f34c6d63-6348.4e50:x"))?.UnitId);
    }
}
