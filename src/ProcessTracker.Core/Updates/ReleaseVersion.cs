namespace ProcessTracker.Core.Updates;

/// <summary>The release number of an informational version; what every version comparison in ProcessTracker uses.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// X.Y.Z of an informational version such as <c>0.3.0</c>, <c>0.3.0-dev.202611231200</c> or <c>0.3.0+hash</c>;
    /// null when it doesn't start with exactly three numbers.
    /// </summary>
    public static Version? Numeric(string informational)
    {
        var core = informational.Split('+', '-')[0];
        return Version.TryParse(core, out var v) && v.Build >= 0 && v.Revision < 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }
}
