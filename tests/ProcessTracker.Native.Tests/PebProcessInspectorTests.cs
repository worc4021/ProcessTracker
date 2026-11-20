using System.Diagnostics;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Native.Tests;

public class PebProcessInspectorTests
{
    private static ProcessKey KeyOfPid(int pid)
        => new NtSnapshotSource().Take().Processes.Single(p => p.Key.Pid == pid).Key;

    private static ProcessKey KeyOf(Process p) => KeyOfPid(p.Id);

    [Fact] public void ReadsAllowlistedClaudeVariablesOfChild()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { CreateNoWindow = true };
        psi.Environment["CLAUDE_CODE_SESSION_ID"] = "sid-123";
        psi.Environment["CLAUDE_CODE_HOST_SESSION_ID"] = "local_abc";
        psi.Environment["CLAUDE_CODE_ENTRYPOINT"] = "cli";
        psi.Environment["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret";
        using var child = Process.Start(psi)!;
        try
        {
            var key = KeyOf(child);
            var r = new PebProcessInspector().Inspect(key);
            Assert.Equal(InspectStatus.Ok, r.Status);
            Assert.Equal("sid-123", r.SessionId);
            Assert.Equal("local_abc", r.HostSessionId);
            Assert.Equal("cli", r.Entrypoint);
            Assert.EndsWith(@"\cmd.exe", r.ImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ping", r.CommandLine);
            Assert.DoesNotContain("secret", r.ToString());
        }
        finally { child.Kill(entireProcessTree: true); }
    }

    [Fact] public void WrongCreateTime_IsGone()
        => Assert.Equal(InspectStatus.Gone, new PebProcessInspector().Inspect(new ProcessKey(Environment.ProcessId, 1)).Status);

    [Fact] public void ExitedProcess_IsGone()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true })!;
        try
        {
            var key = KeyOf(child);
            child.WaitForExit();
            Thread.Sleep(100);
            Assert.Equal(InspectStatus.Gone, new PebProcessInspector().Inspect(key).Status);
        }
        finally { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    [Fact] public void SystemProcess_IsAccessDeniedOrUnsupported_NotThrow()
    {
        var r = new PebProcessInspector().Inspect(KeyOfPid(4));
        Assert.NotEqual(InspectStatus.Ok, r.Status);
    }
}
