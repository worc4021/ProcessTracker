using System.CommandLine;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Cli.Commands;

public static class TreeCommand
{
    public static Command Create()
    {
        var unit = new Argument<string?>("unit") { Arity = ArgumentArity.ZeroOrOne, Description = "Session unit id (default: this session)." };
        var json = CommandSupport.JsonOption();
        var cmd = new Command("tree", "Show a session's process tree.") { unit, json };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, async api =>
        {
            var unitId = pr.GetValue(unit);
            if (unitId is null)
            {
                var caller = await api.CallerAsync(ct);
                unitId = caller is { Kind: AttributionKind.Session } ? caller.UnitId : null;
                if (unitId is null)
                {
                    await Console.Error.WriteLineAsync("Not running inside a Claude session; specify a unit id (see 'proctrack sessions').");
                    return CommandSupport.Refused;
                }
            }

            var tree = await api.SessionTreeAsync(unitId, ct);
            if (tree is null)
            {
                await Console.Error.WriteLineAsync($"Unknown session unit '{unitId}'.");
                return CommandSupport.Refused;
            }
            if (pr.GetValue(json)) { Console.WriteLine(Format.Json(tree)); return CommandSupport.Ok; }

            var now = DateTime.UtcNow;
            var s = tree.Session;
            Console.WriteLine($"{s.Title ?? s.UnitId}  ({s.ProcessCount} processes, {Format.Cores(s.CpuCores)} cores, {Format.Gigabytes(s.PrivateBytes)} GB)");
            var rows = new List<string[]>();
            void Walk(ProcessTreeNodeDto n, int depth)
            {
                var row = Format.ProcessRow(n.Process, now);
                row[1] = Format.Truncate(new string(' ', depth * 2) + n.Process.ImageName, 28);
                rows.Add(row);
                foreach (var c in n.Children) Walk(c, depth + 1);
            }
            foreach (var root in tree.Processes) Walk(root, 0);
            Console.Write(Format.Table(Format.ProcessHeaders, rows, Format.ProcessRight));
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
