using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcessTracker.Native;

/// <summary>
/// The named event a user-mode instance waits on to exit cleanly. The MSI (as SYSTEM, before it starts the machine
/// service) and a newer user-mode version taking over set it. Only the creating user, SYSTEM and Administrators may
/// open it, so one user cannot stop another user's instance.
/// </summary>
public static class UserModeSignal
{
    /// <summary>The exit event a running user-mode instance owns.</summary>
    public const string ExitEventName = @"Global\ProcessTracker.UserMode.Exit";

    /// <summary>The single-instance mutex shared by the service and user mode; a stopped instance releases it.</summary>
    public const string InstanceMutexName = @"Global\ProcessTracker.Service";

    /// <summary>
    /// Creates (or opens) the manual-reset exit event for the instance that now holds the single-instance mutex, and
    /// resets it: a signal left set for a previous instance that is still closing its handle is not for us.
    /// Throws <see cref="UnauthorizedAccessException"/> when the event already exists under a DACL other than ours, or
    /// is owned by anyone but this user, SYSTEM or Administrators: another user pre-creating the name (even with our
    /// exact DACL, keeping a handle of their own) must not be able to stop this instance.
    /// </summary>
    public static EventWaitHandle CreateForInstance(string name = ExitEventName) => CreateForInstance(name, trustedOwners: null);

    /// <param name="name">The event name.</param>
    /// <param name="trustedOwners">Owners an existing event may have; null for this user, SYSTEM and Administrators.
    /// Tests narrow it, as one process cannot create an object owned by another account.</param>
    internal static EventWaitHandle CreateForInstance(string name, IReadOnlyCollection<SecurityIdentifier>? trustedOwners)
    {
        var user = CurrentUser();
        var allowed = AllowedSids(user);
        var handle = EventWaitHandleAcl.Create(initialState: false, EventResetMode.ManualReset, name, out var createdNew,
            OwnerOnlySecurity(user));
        if (!createdNew && !IsOurs(handle, allowed, trustedOwners ?? allowed))
        {
            handle.Dispose();
            throw new UnauthorizedAccessException($@"{name} exists but was not created by a ProcessTracker instance of this user.");
        }
        handle.Reset();
        return handle;
    }

    /// <summary>
    /// Sets the exit event if an instance created it. False when there is none. Throws
    /// <see cref="UnauthorizedAccessException"/> when it belongs to another user and the caller is not SYSTEM or an
    /// administrator.
    /// </summary>
    public static bool TrySignal(string name = ExitEventName)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current process has no user SID.");

    private static SecurityIdentifier[] AllowedSids(SecurityIdentifier owner) =>
    [
        owner,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    private static EventWaitHandleSecurity OwnerOnlySecurity(SecurityIdentifier owner)
    {
        var security = new EventWaitHandleSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids(owner))
            security.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// True when the event's owner is one of <paramref name="trustedOwners"/> and every allow entry of its DACL names
    /// one of <paramref name="allowed"/>.
    /// </summary>
    private static bool IsOurs(EventWaitHandle handle, SecurityIdentifier[] allowed, IReadOnlyCollection<SecurityIdentifier> trustedOwners)
    {
        var security = handle.GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trustedOwners.Contains(owner))
            return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return rules.Cast<EventWaitHandleAccessRule>()
            .All(r => r.AccessControlType == AccessControlType.Deny || allowed.Contains((SecurityIdentifier)r.IdentityReference));
    }
}
