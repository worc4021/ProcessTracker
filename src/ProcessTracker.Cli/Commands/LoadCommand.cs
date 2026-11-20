using System.CommandLine;
using System.Globalization;

namespace ProcessTracker.Cli.Commands;

public static class LoadCommand
{
    public static Command Create()
    {
        var json = CommandSupport.JsonOption();
        var cmd = new Command("load", "Machine load and the sessions using the most.") { json };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, async api =>
        {
            var r = await api.LoadAsync(ct);
            if (pr.GetValue(json)) { Console.WriteLine(Format.Json(r)); return CommandSupport.Ok; }

            var inv = CultureInfo.InvariantCulture;
            Console.WriteLine($"Pressure:              {r.Pressure}");
            Console.WriteLine($"CPU busy:              {r.CpuBusyPercent.ToString("F0", inv)} %");
            Console.WriteLine($"Free cores:            {Format.Cores(r.FreeCores)}");
            Console.WriteLine($"Available memory:      {Format.Gigabytes(r.AvailableGb)} GB");
            Console.WriteLine($"Commit headroom:       {Format.Gigabytes(r.CommitHeadroomGb)} GB");
            Console.WriteLine($"Suggested parallelism: {r.SuggestedParallelism}");
            if (r.Caller is { } c)
                Console.WriteLine($"This session:          {Format.Cores(c.CpuCores)} cores, {Format.Gigabytes(c.PrivateGb)} GB, {c.ProcessCount} processes");
            Console.WriteLine();
            var rows = r.TopSessions.Select(s => new[]
            {
                Format.Truncate(s.Title ?? s.UnitId, 40), Format.Cores(s.CpuCores), Format.Gigabytes(s.PrivateGb),
                s.ProcessCount.ToString(inv),
            }).ToList();
            Console.Write(Format.Table(["SESSION", "CPU", "MEM_GB", "PROCS"], rows, "CPU", "MEM_GB", "PROCS"));
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
