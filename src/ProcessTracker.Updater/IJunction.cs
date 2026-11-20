namespace ProcessTracker.Updater;

/// <summary>A directory junction such as <c>&lt;root&gt;\current</c>.</summary>
public interface IJunction
{
    /// <summary>The directory the junction points at, or null when <paramref name="link"/> is not a junction.</summary>
    string? Target(string link);

    /// <summary>Points <paramref name="link"/> at <paramref name="target"/>, replacing any existing junction.</summary>
    void Point(string link, string target);
}
