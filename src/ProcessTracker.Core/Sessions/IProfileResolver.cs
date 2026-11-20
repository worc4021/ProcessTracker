using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Sessions;

/// <summary>Finds the user profile directory of the account that owns a process.</summary>
public interface IProfileResolver
{
    /// <summary>The owner's profile path; null when the process is gone or its owner has no profile.</summary>
    string? ProfileOf(ProcessKey key);
}
