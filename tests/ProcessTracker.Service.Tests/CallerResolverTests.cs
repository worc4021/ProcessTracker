using System.Collections.Frozen;
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

    private static ProcessView Proc(int pid, Attribution attribution)
    {
        var key = new ProcessKey(pid, 1_000_000 + pid);
        var record = new ProcessRecord(key, null, "claude.exe", null, null, attribution, T0, null);
        return new ProcessView(record, new ProcessSample(key, T0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), default, null, default, false);
    }

    private void SetProcesses(params ProcessView[] processes) =>
        _host.View = new TrackerView(T0, default, processes.ToFrozenDictionary(p => p.Record.Key), []);

    private static DefaultHttpContext Request(string? sessionHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemotePort = ClientPort;
        ctx.Connection.LocalPort = ServerPort;
        if (sessionHeader is not null) ctx.Request.Headers["X-Claude-Session"] = sessionHeader;
        return ctx;
    }

    /// <summary>A resolver whose TCP table maps (ClientPort, ServerPort) to <paramref name="ownerPid"/>.</summary>
    private CallerResolver Resolver(int? ownerPid) =>
        new(_host, (client, server) => client == ClientPort && server == ServerPort ? ownerPid : null);

    [Fact]
    public void Resolver_TcpOwnerIsSessionProcess_ReturnsIt()
    {
        var session = new Attribution(AttributionKind.Session, "host:local_a", "cc-1", true);
        SetProcesses(Proc(42, session), Proc(43, Attribution.Unattributed));

        // The header is ignored when the connection already resolves to a session.
        var caller = Resolver(42).Resolve(Request(sessionHeader: "local_other"));

        Assert.Equal(session, caller);
    }

    [Fact]
    public void Resolver_TcpOwnerIsClaudeApp_FallsBackToHeader()
    {
        SetProcesses(Proc(42, new Attribution(AttributionKind.ClaudeApp, null, null, false)));

        var caller = Resolver(42).Resolve(Request(sessionHeader: "local_b"));

        Assert.Equal(new Attribution(AttributionKind.Session, "host:local_b", null, false), caller);
    }

    [Fact]
    public void Resolver_NoMatch_NoHeader_Null()
    {
        SetProcesses(Proc(42, Attribution.Unattributed));

        Assert.Null(Resolver(ownerPid: null).Resolve(Request()));
    }
}
