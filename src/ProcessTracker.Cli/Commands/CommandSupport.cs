using System.CommandLine;
using System.Text.Json;

namespace ProcessTracker.Cli.Commands;

/// <summary>Options and error handling shared by all commands.</summary>
public static class CommandSupport
{
    public const int Ok = 0, Refused = 1, Unreachable = 2;

    public static Option<bool> DevOption { get; } = new("--dev") { Description = "Use the dev service (port 47812).", Recursive = true };

    public static Option<bool> JsonOption() => new("--json") { Description = "Print JSON instead of a table." };

    /// <summary>Runs <paramref name="body"/> with a client; maps unreachable/unexpected service answers to exit codes.</summary>
    public static Task<int> RunAsync(ParseResult pr, Func<ApiClient, Task<int>> body)
    {
        var dev = pr.GetValue(DevOption);
        return ExecuteAsync(() => new ApiClient(ApiClient.ResolveBaseUrl(dev)), body);
    }

    /// <summary>Creates the client and runs the body inside the error mapping, so no failure escapes as a stack trace.</summary>
    public static async Task<int> ExecuteAsync(Func<ApiClient> create, Func<ApiClient, Task<int>> body)
    {
        try
        {
            using var client = create();
            return await body(client);
        }
        catch (ServiceUnreachableException e) { return await Report(e.Message, Unreachable); }
        catch (ApiException e) { return await Report(e.Message, Refused); }
        catch (OperationCanceledException) { return await Report("Cancelled.", Refused); }
        catch (UriFormatException e) { return await Report($"Invalid PROCTRACK_URL: {e.Message}", Refused); }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException)
        {
            return await Report($"Unexpected answer or I/O failure: {e.Message}", Refused);
        }
    }

    private static async Task<int> Report(string message, int code)
    {
        await Console.Error.WriteLineAsync(message);
        return code;
    }
}
