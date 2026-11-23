using System.Security.Principal;

namespace ProcessTracker.Native.Tests;

public class ProcessOwnerTests
{
    [Fact] public void SidOf_CurrentProcess_IsCurrentUser()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.Equal(me.User, ProcessOwner.SidOf(Environment.ProcessId));
    }

    [Fact] public void SidOf_NonExistentPid_IsNull() => Assert.Null(ProcessOwner.SidOf(int.MaxValue - 3));
}
