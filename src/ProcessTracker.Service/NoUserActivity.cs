using ProcessTracker.Core.Hooks;

namespace ProcessTracker.Service;

/// <summary>
/// Service mode's <see cref="IUserActivity"/>: the machine service runs in session 0, where the last input of the
/// interactive user cannot be read, so it always reports "unknown".
/// </summary>
public sealed class NoUserActivity : IUserActivity
{
    public TimeSpan? SinceLastInput() => null;
}
