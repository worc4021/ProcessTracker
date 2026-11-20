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
                Format.Truncate(s.Title ?? s.UnitId, 40), Format.Cores(s.CpuCores), Format.Gigabytes(s.PrivateBytes),
                s.ProcessCount.ToString(CultureInfo.InvariantCulture),
                Format.Idle(s.LastTranscriptWriteUtc, now), s.IsLive ? "live" : "ended", s.UnitId,
            }).ToList();
            Console.Write(Format.Table(
                ["SESSION", "CPU", "MEM_GB", "PROCS", "IDLE", "STATE", "UNIT"], rows, "CPU", "MEM_GB", "PROCS", "IDLE"));
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
