using System.CommandLine;
using System.Globalization;

namespace ProcessTracker.Cli.Commands;

public static class HistoryCommand
{
    public static Command Create()
    {
        var unit = new Option<string?>("--unit") { Description = "Session unit id." };
        var key = new Option<string?>("--key") { Description = "Process key (pid@ticks)." };
        var minutes = new Option<int>("--minutes") { Description = "Window length in minutes.", DefaultValueFactory = _ => 30 };
        var json = CommandSupport.JsonOption();
        var cmd = new Command("history", "Show CPU/memory/IO history of a session or a process.") { unit, key, minutes, json };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, async api =>
        {
            var (u, k, m) = (pr.GetValue(unit), pr.GetValue(key), pr.GetValue(minutes));
            if ((u is null) == (k is null) || m <= 0)
            {
                await Console.Error.WriteLineAsync("Specify exactly one of --unit or --key, and --minutes > 0.");
                return CommandSupport.Refused;
            }

            var to = DateTime.UtcNow;
            var series = await api.HistoryAsync(u, k, to.AddMinutes(-m), to, ct);
            if (series is null)
            {
                await Console.Error.WriteLineAsync("Nothing known for that session or process.");
                return CommandSupport.Refused;
            }
            if (pr.GetValue(json)) { Console.WriteLine(Format.Json(series)); return CommandSupport.Ok; }

            Console.WriteLine($"{series.Name ?? series.UnitId ?? series.Key}  {series.FromUtc:HH:mm:ss}Z - {series.ToUtc:HH:mm:ss}Z (UTC)");
            var rows = series.Points.Select(p => new[]
            {
                p.TimestampUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Format.Cores(p.CpuCores), Format.Gigabytes(p.PrivateBytes),
                Format.Megabytes(p.ReadBytesDelta + p.WriteBytesDelta),
                p.ProcessCount?.ToString(CultureInfo.InvariantCulture) ?? "-",
            }).ToList();
            Console.Write(Format.Table(["TIME_UTC", "CPU", "MEM_GB", "IO_MB", "PROCS"], rows, "CPU", "MEM_GB", "IO_MB", "PROCS"));
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
