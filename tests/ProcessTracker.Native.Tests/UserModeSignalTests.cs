using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcessTracker.Native.Tests;

/// <summary>Uses a test-specific event name per test, never the real <see cref="UserModeSignal.ExitEventName"/> (ruling R1).</summary>
public class UserModeSignalTests
{
    private static string TestEventName() => $@"Global\ProcessTracker.Test.{Guid.NewGuid():N}";

    [Fact]
    public void Signal_SetsEventCreatedByInstance()
    {
        var name = TestEventName();
        using var instance = UserModeSignal.CreateForInstance(name);

        Assert.True(UserModeSignal.TrySignal(name));
        Assert.True(instance.WaitOne(0));
    }

    [Fact]
    public void Signal_NoInstance_ReturnsFalse()
    {
        Assert.False(UserModeSignal.TrySignal(TestEventName()));
    }

    [Fact]
    public void CreateForInstance_ResetsStaleSignal()
    {
        var name = TestEventName();
        using var previous = UserModeSignal.CreateForInstance(name);
        previous.Set(); // the previous instance was asked to exit but still holds its handle

        using var next = UserModeSignal.CreateForInstance(name);

        Assert.False(next.WaitOne(0));
        Assert.False(previous.WaitOne(0)); // same kernel object
    }

    [Fact]
    public void CreateForInstance_GrantsOnlyOwnerSystemAndAdministrators()
    {
        using var instance = UserModeSignal.CreateForInstance(TestEventName());

        var rules = instance.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<EventWaitHandleAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(
            new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" }.Order(),
            rules.Select(r => r.IdentityReference.Value).Order());
    }

    [Fact]
    public void CreateForInstance_ExistingEventOpenToOthers_Throws()
    {
        // Another user pre-creating the name with a permissive DACL must not get a handle that can stop our instance.
        var name = TestEventName();
        var everyone = new EventWaitHandleSecurity();
        everyone.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        using var squatter = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out _, everyone);

        Assert.Throws<UnauthorizedAccessException>(() => UserModeSignal.CreateForInstance(name));
    }

    [Fact]
    public void CreateForInstance_IsManualReset()
    {
        var name = TestEventName();
        using var instance = UserModeSignal.CreateForInstance(name);
        UserModeSignal.TrySignal(name);

        Assert.True(instance.WaitOne(0));
        Assert.True(instance.WaitOne(0)); // stays set for every waiter
    }
}
