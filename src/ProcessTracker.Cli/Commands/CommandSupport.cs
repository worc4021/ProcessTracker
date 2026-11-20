using System.CommandLine;

namespace ProcessTracker.Cli.Commands;

/// <summary>Options and error handling shared by all commands.</summary>
public static class CommandSupport
{
    public const int Ok = 0, Refused = 1, Unreachable = 2;

    public static Option<bool> DevOption { get; } = new("--dev") { Description = "Use the dev service (port 47812).", Recursive = true };

    public static Option<bool> JsonOption() => new("--json") { Description = "Print JSON instead of a table." };

    /// <summary>Runs <paramref name="body"/> with a client; maps unreachable/unexpected service answers to exit codes.</summary>
    public static async Task<int> RunAsync(ParseResult pr, Func<ApiClient, Task<int>> body)
    {
        using var client = new ApiClient(ApiClient.ResolveBaseUrl(pr.GetValue(DevOption)));
        try { return await body(client); }
        catch (ServiceUnreachableException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return Unreachable;
        }
        catch (ApiException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return Refused;
        }
    }
}
