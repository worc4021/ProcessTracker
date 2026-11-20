using ProcessTracker.Core.Model;

namespace ProcessTracker.Native.Tests;

public class ProfileResolverTests
{
    private static ProcessKey KeyOfPid(int pid)
        => new NtSnapshotSource().Take().Processes.Single(p => p.Key.Pid == pid).Key;

    [Fact]
    public void ProfileOf_CurrentProcess_IsCurrentUserProfile()
    {
        var profile = new ProfileResolver().ProfileOf(KeyOfPid(Environment.ProcessId));

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), profile,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileOf_WrongCreateTime_Null()
    {
        var key = KeyOfPid(Environment.ProcessId);

        Assert.Null(new ProfileResolver().ProfileOf(key with { CreateTimeTicks = key.CreateTimeTicks + 1 }));
    }
}
