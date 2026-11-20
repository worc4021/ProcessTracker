using System.Globalization;

namespace ProcessTracker.Core.Model;

/// <summary>Process identity: PID plus creation time (FILETIME UTC ticks). Never key by PID alone.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTimeTicks)
{
    public override string ToString() => $"{Pid}@{CreateTimeTicks}";

    public static bool TryParse(string s, out ProcessKey key)
    {
        key = default;
        if (s is null) return false;
        var at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        if (!int.TryParse(s.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
        if (!long.TryParse(s.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        key = new ProcessKey(pid, ticks);
        return true;
    }
}
