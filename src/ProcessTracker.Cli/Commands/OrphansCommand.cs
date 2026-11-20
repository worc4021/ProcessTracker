using System.CommandLine;

namespace ProcessTracker.Cli.Commands;

public static class OrphansCommand
{
    public static Command Create()
    {
        var json = CommandSupport.JsonOption();
        var cmd = new Command("orphans", "List orphaned processes (alertable first). A trailing ! marks an alert.") { json };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, async api =>
        {
            var orphans = await api.OrphansAsync(ct);
            if (pr.GetValue(json)) { Console.WriteLine(Format.Json(orphans)); return CommandSupport.Ok; }

            var now = DateTime.UtcNow;
            Console.Write(Format.Table(Format.ProcessHeaders, orphans.Select(p => Format.ProcessRow(p, now)).ToList(), Format.ProcessRight));
            return CommandSupport.Ok;
        }));
        return cmd;
    }
}
