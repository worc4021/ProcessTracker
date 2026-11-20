using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using ProcessTracker.Core.Contracts;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Cli.Commands;

/// <summary>
/// The only place a process is ever killed, with the user's own rights. Order: look up the target, re-verify its
/// create time, apply the guard, confirm, kill, audit.
/// </summary>
public static class KillCommand
{
    public static Command Create()
    {
        var pidArg = new Argument<int>("pid") { Description = "Process id to kill." };
        var tree = new Option<bool>("--tree") { Description = "Also kill all descendants." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var cmd = new Command("kill", "Kill a tracked process (guarded when run from inside a Claude session).") { pidArg, tree, yes };
        cmd.SetAction((pr, ct) => CommandSupport.RunAsync(pr, api =>
            KillAsync(api, pr.GetValue(pidArg), pr.GetValue(tree), pr.GetValue(yes), ct)));
        return cmd;
    }

    private static async Task<int> KillAsync(ApiClient api, int pid, bool tree, bool yes, CancellationToken ct)
    {
        // 1. Target, as tracked by the service.
        var targets = await api.ProcessesByPidAsync(pid, ct);
        if (targets.Length == 0)
            return await Fail($"PID {pid} is not a tracked process.");
        var target = targets[0];
        if (!ProcessKey.TryParse(target.Key, out var key))
            return await Fail($"Service returned an invalid key '{target.Key}'.");

        // 2. PID reuse guard: the live process must still be the one the service knows.
        Process live;
        try
        {
            live = Process.GetProcessById(pid);
            if (live.StartTime.ToUniversalTime().ToFileTimeUtc() != key.CreateTimeTicks)
                return await Fail($"PID {pid} now belongs to a different process (create time differs); nothing killed.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return await Fail($"PID {pid} cannot be inspected ({e.Message}); nothing killed.");
        }

        // 3. Guard. Tree members are the target's descendants as known to the service: every process listed in
        // a session tree or as an orphan whose parent chain leads to the target.
        var members = tree ? await DescendantsAsync(api, target, ct) : [];
        await LetOwnProcessBeSampledAsync(ct);
        var caller = await api.CallerAsync(ct) is { } dto ? new Attribution(dto.Kind, dto.UnitId, dto.ClaudeSessionId, dto.IsAnchor) : null;
        var decision = KillGuard.Check(caller, target, tree, members);
        if (!decision.Allowed)
            return await Fail(decision.Reason!);

        // 4. Confirm.
        if (!yes)
        {
            var what = tree ? $"{target.ImageName} (PID {pid}) and {members.Count} descendant(s)" : $"{target.ImageName} (PID {pid})";
            Console.Write($"Kill {what}? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!"y".Equals(answer, StringComparison.OrdinalIgnoreCase) && !"yes".Equals(answer, StringComparison.OrdinalIgnoreCase))
                return await Fail("Declined; nothing killed.");
        }

        // 5. Kill.
        string result;
        try
        {
            live.Kill(entireProcessTree: tree);
            result = "killed";
            Console.WriteLine($"Killed {target.ImageName} (PID {pid}){(tree ? " and its process tree" : "")}.");
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            result = $"failed: {e.Message}";
            await Console.Error.WriteLineAsync($"Kill failed: {e.Message}");
        }

        // 6. Audit. A failed audit never changes the outcome of the kill.
        try
        {
            await api.PostKillAuditAsync(new KillAuditDto(
                target.Key, target.ImageName, target.Attribution.UnitId,
                Path.GetFileName(Environment.ProcessPath) ?? "proctrack", tree, result), ct);
        }
        catch (Exception e) when (e is ServiceUnreachableException or ApiException)
        {
            await Console.Error.WriteLineAsync($"Warning: audit record not stored: {e.Message}");
        }
        return result == "killed" ? CommandSupport.Ok : CommandSupport.Refused;
    }

    /// <summary>
    /// The service attributes the caller from its own process, which it only knows after a sampling tick (2 s).
    /// Waiting makes the guard independent of the X-Claude-Session header, which an agent could simply omit.
    /// </summary>
    private static async Task LetOwnProcessBeSampledAsync(CancellationToken ct)
    {
        var remaining = TimeSpan.FromSeconds(3.5) - (DateTime.Now - Process.GetCurrentProcess().StartTime);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }

    private static async Task<int> Fail(string message)
    {
        await Console.Error.WriteLineAsync(message);
        return CommandSupport.Refused;
    }

    /// <summary>Transitive children of <paramref name="target"/> among all processes the service lists (session trees + orphans).</summary>
    private static async Task<IReadOnlyList<ProcessViewDto>> DescendantsAsync(ApiClient api, ProcessViewDto target, CancellationToken ct)
    {
        var all = new Dictionary<string, ProcessViewDto>();
        foreach (var o in await api.OrphansAsync(ct)) all[o.Key] = o;
        foreach (var s in await api.SessionsAsync(ct))
        {
            if (await api.SessionTreeAsync(s.UnitId, ct) is not { } t) continue;
            void Add(ProcessTreeNodeDto n)
            {
                all[n.Process.Key] = n.Process;
                foreach (var c in n.Children) Add(c);
            }
            foreach (var r in t.Processes) Add(r);
        }

        var byParent = all.Values.Where(p => p.ParentKey is not null).ToLookup(p => p.ParentKey!);
        var result = new List<ProcessViewDto>();
        var pending = new Stack<string>([target.Key]);
        var seen = new HashSet<string> { target.Key };
        while (pending.Count > 0)
            foreach (var child in byParent[pending.Pop()])
                if (seen.Add(child.Key)) { result.Add(child); pending.Push(child.Key); }
        return result;
    }
}
