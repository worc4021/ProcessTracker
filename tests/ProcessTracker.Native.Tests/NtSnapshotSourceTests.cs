using System.Diagnostics;

namespace ProcessTracker.Native.Tests;

public class NtSnapshotSourceTests
{
    [Fact] public void ContainsCurrentProcess_WithMatchingCreateTime()
    {
        var snap = new NtSnapshotSource().Take();
        using var me = Process.GetCurrentProcess();
        var row = Assert.Single(snap.Processes, p => p.Key.Pid == me.Id);
        Assert.Equal(me.StartTime.ToUniversalTime().ToFileTimeUtc(), row.Key.CreateTimeTicks);
        Assert.InRange(row.ThreadCount, me.Threads.Count - 4, me.Threads.Count + 4);
        Assert.True(row.PrivateBytes > 0 && row.KernelTime + row.UserTime > 0);
    }

    [Fact] public void ParentPidOfChild_IsUs()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { CreateNoWindow = true })!;
        var row = new NtSnapshotSource().Take().Processes.Single(p => p.Key.Pid == child.Id);
        Assert.Equal(Environment.ProcessId, row.ParentPid);
        child.Kill();
    }

    [Fact] public void SystemCounters_AreSane()
    {
        var s = new NtSnapshotSource().Take().System;
        Assert.True(s.CommitLimitBytes > s.CommitChargeBytes && s.CommitChargeBytes > 0);
        Assert.True(s.TotalPhysicalBytes >= s.AvailablePhysicalBytes);
    }

    [Fact] public void TwoConsecutiveSnapshots_Under50ms()   // budget guard
    {
        var src = new NtSnapshotSource(); src.Take();
        var sw = Stopwatch.StartNew(); src.Take();
        Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms");
    }
}
