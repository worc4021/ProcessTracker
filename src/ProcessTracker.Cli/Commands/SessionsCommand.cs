using System.CommandLine;
using System.Globalization;

namespace ProcessTracker.Cli.Commands;

public static class SessionsCommand
{
    public static Command Create()
    {
        var json = CommandSupport.JsonOption();
        var cmd = new Command("sessions", "List Claude Code sessions with their totals.") { json };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, async api =>
        {
            var sessions = await api.SessionsAsync(ct);
            if (pr.GetValue(json)) { Console.WriteLine(Format.Json(sessions)); return CommandSupport.Ok; }

            var now = DateTime.UtcNow;
            var rows = sessions.Select(s => new[]
            {
                Format.Truncate(s.Title ?? s.UnitId, 40), Format.Truncate(s.Project ?? "-", 24),
                Format.Truncate(s.Branch ?? "-", 28), Format.Cores(s.CpuCores), Format.Gigabytes(s.PrivateBytes),
                s.ProcessCount.ToString(CultureInfo.InvariantCulture),
                Format.Idle(s.LastTranscriptWriteUtc, now), s.IsLive ? "live" : "ended", s.UnitId,
            }).ToList();
            string[] headers = ["SESSION", "PROJECT", "BRANCH", "CPU", "MEM_GB", "PROCS", "IDLE", "STATE", "UNIT"];
            var table = Format.Table(headers, rows, "CPU", "MEM_GB", "PROCS", "IDLE");

            // BRANCH only when there is one to show and the table still fits the console (always when redirected).
            var anyBranch = sessions.Any(s => !string.IsNullOrEmpty(s.Branch));
            if (!anyBranch || Format.ConsoleWidth() is { } width && Format.MaxLineLength(table) > width)
            {
                const int branch = 2;
                table = Format.Table(headers.Where((_, i) => i != branch).ToArray(),
                    rows.Select(r => r.Where((_, i) => i != branch).ToArray()).ToList(), "CPU", "MEM_GB", "PROCS", "IDLE");
            }
            Console.Write(table);
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
