using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcessTracker.Updater;

/// <summary>
/// <c>ProcessTracker.Updater.exe --root "C:\Program Files\ProcessTracker" --version &lt;v&gt; [--timeout 15]</c>.
/// Exit codes: 0 Upgraded, 1 RolledBack, 2 RollbackFailed, 3 invalid arguments or a failure before anything changed.
/// Run it elevated and from outside the install root (deploy.ps1 copies it to %TEMP%).
/// </summary>
public static partial class Program
{
    public const string HealthUrl = "http://127.0.0.1:47811/api/health";
    public const int NothingChanged = 3;

    private const string Usage =
        "Usage: ProcessTracker.Updater.exe --root <install root> --version <version> [--timeout <seconds, default 15>]";

    public static async Task<int> Main(string[] args)
    {
        if (Parse(args) is not { } options)
        {
            Console.Error.WriteLine(Usage);
            return NothingChanged;
        }

        using var probe = new HttpHealthProbe(new Uri(HealthUrl));
        var orchestrator = new UpgradeOrchestrator(new WindowsServiceControl(), new CmdJunction(), probe, Log);
        try
        {
            var outcome = await orchestrator.RunAsync(options.Root, options.Version, options.Timeout);
            Console.WriteLine($"Outcome: {outcome}");
            return (int)outcome;
        }
        catch (Exception e) // RunAsync handles every failure after it starts changing things; this is e.g. a missing folder
        {
            Console.Error.WriteLine($"Nothing changed: {e.Message}");
            return NothingChanged;
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}");

    private sealed record Options(string Root, string Version, TimeSpan Timeout);

    /// <summary>The parsed options, or null (after printing why) when the arguments are invalid.</summary>
    private static Options? Parse(string[] args)
    {
        string? root = null, version = null;
        var timeout = TimeSpan.FromSeconds(15);
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {name}.");
                return null;
            }
            var value = args[i + 1];
            switch (name)
            {
                case "--root":
                    root = Path.GetFullPath(value);
                    break;
                case "--version":
                    // The version names a folder under versions\ and ends up on a cmd.exe command line: a plain name only.
                    if (!VersionPattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Invalid version '{value}'.");
                        return null;
                    }
                    version = value;
                    break;
                case "--timeout":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        || !double.IsFinite(seconds) || seconds <= 0 || seconds > 600)
                    {
                        Console.Error.WriteLine($"Invalid timeout '{value}' (seconds, greater than 0 and at most 600).");
                        return null;
                    }
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option {name}.");
                    return null;
            }
        }
        if (root is null || version is null)
        {
            Console.Error.WriteLine("--root and --version are required.");
            return null;
        }
        return new Options(root, version, timeout);
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]*$")]
    private static partial Regex VersionPattern();
}
