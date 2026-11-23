namespace ProcessTracker.Service;

/// <summary>
/// How the service exe runs. Service: the MSI's Windows service (LocalSystem). User: as the logged-in user, started by
/// the per-user logon task (--user). Dev: by hand for development (--dev; port 47812, no mutex).
/// </summary>
public enum RunMode { Service, User, Dev }

public static class RunModes
{
    /// <summary>Maps the short flags to configuration keys: --dev, --user and --console become Dev/User/Console=true.</summary>
    public static string[] NormalizeFlags(string[] args) => args.Select(a => a switch
    {
        "--dev" => "--Dev=true",
        "--user" => "--User=true",
        "--console" => "--Console=true",
        _ => a,
    }).ToArray();

    /// <summary>The run mode from the command-line configuration; throws <see cref="InvalidOperationException"/> for User with Dev.</summary>
    public static RunMode ParseMode(IConfiguration mode)
    {
        var user = mode.GetValue<bool>("User");
        var dev = mode.GetValue<bool>("Dev");
        if (user && dev) throw new InvalidOperationException("--user and --dev cannot be combined");
        return user ? RunMode.User : dev ? RunMode.Dev : RunMode.Service;
    }

    /// <summary>The name <c>/api/health</c> reports: service, user or dev.</summary>
    public static string WireName(this RunMode mode) => mode switch
    {
        RunMode.Service => "service",
        RunMode.User => "user",
        RunMode.Dev => "dev",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
