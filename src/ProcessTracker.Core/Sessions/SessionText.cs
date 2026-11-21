using System.Text;

namespace ProcessTracker.Core.Sessions;

/// <summary>
/// Hygiene for free text taken from session files (titles, summaries, folders): it is shown in terminals, the
/// dashboard and MCP results, so control characters (C0, DEL, C1: terminal escape sequences) and bidirectional
/// formatting characters (Trojan Source style reordering) never pass through.
/// </summary>
public static class SessionText
{
    /// <summary>
    /// Bidirectional embedding, override and isolate controls and the implicit marks: LRM, RLM, ALM, LRE..RLO, LRI..PDI.
    /// Written as escapes on purpose: literal bidi characters in source are themselves a Trojan Source hazard.
    /// </summary>
    public static bool IsBidiControl(char c) =>
        c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// <summary>True for characters that must never reach a terminal or page: controls and bidi controls.</summary>
    public static bool IsUnsafe(char c) => char.IsControl(c) || IsBidiControl(c);

    /// <summary>
    /// One line of at most <paramref name="max"/> characters: whitespace and control characters collapse to single
    /// spaces, bidirectional controls are dropped, a trailing surrogate pair is never split. Null when empty.
    /// </summary>
    public static string? Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || max <= 0) return null;
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        var space = false;
        foreach (var ch in s)
        {
            if (IsBidiControl(ch)) continue;
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length > max) break;
        }
        if (sb.Length == 0) return null;
        if (sb.Length > max)
            sb.Length = char.IsHighSurrogate(sb[max - 1]) ? max - 1 : max;
        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }
}
