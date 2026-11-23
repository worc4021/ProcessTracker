namespace ProcessTracker.Core.Hooks;

/// <summary>How long ago the interactive user last touched the keyboard or mouse.</summary>
public interface IUserActivity
{
    /// <summary>Time since the last keyboard or mouse input, or null when it cannot be known (as a service in session 0).</summary>
    TimeSpan? SinceLastInput();
}
