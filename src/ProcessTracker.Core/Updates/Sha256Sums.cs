using System.Text.RegularExpressions;

namespace ProcessTracker.Core.Updates;

/// <summary>Parser for the release's <c>SHA256SUMS.txt</c> (<c>&lt;hex&gt;  &lt;file name&gt;</c> per line).</summary>
public static partial class Sha256Sums
{
    [GeneratedRegex(@"^([0-9a-fA-F]{64})  (\S.*)$")]
    private static partial Regex LinePattern();

    /// <summary>Parses the sums file into lower-case hex by file name; blank lines are ignored.</summary>
    /// <exception cref="FormatException">A non-blank line is not in the two-space format.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = LinePattern().Match(line);
            if (!m.Success) throw new FormatException($"Malformed SHA256SUMS line: '{line}'");
            result[m.Groups[2].Value] = m.Groups[1].Value.ToLowerInvariant();
        }
        return result;
    }
}
