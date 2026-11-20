using System.Buffers;
using System.Text.Json;

namespace ProcessTracker.Core.Sessions;

public sealed record TranscriptInfo(string? Cwd, string? Title, DateTime LastWriteUtc);

/// <summary>
/// Reads cwd and a title from a Claude Code transcript (<c>.claude\projects\*\&lt;id&gt;.jsonl</c>). Single-threaded use.
/// </summary>
public sealed class TranscriptReader
{
    private const int MaxBytes = 256 * 1024;
    private const int Chunk = 32 * 1024;
    private const int MaxTitle = 80;
    private const int MaxPath = 400;
    private const int MaxCachedPaths = 1024;
    private readonly string _projectsDir;
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public TranscriptReader(string userProfilePath) =>
        _projectsDir = Path.Combine(userProfilePath, ".claude", "projects");

    /// <summary>
    /// The first cwd and the first real user prompt (as title) within the first 256 KB; reading stops as soon as
    /// both are found. Null when the transcript does not exist or cannot be read.
    /// </summary>
    public TranscriptInfo? Read(string claudeSessionId)
    {
        try
        {
            if (Locate(claudeSessionId) is not { } file) return null;

            var lastWrite = File.GetLastWriteTimeUtc(file);
            string? cwd = null, title = null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = ArrayPool<byte>.Shared.Rent(MaxBytes);
            try
            {
                int n = 0, start = 0;
                var eof = false;
                while (cwd is null || title is null)
                {
                    var nl = Array.IndexOf(buf, (byte)'\n', start, n - start);
                    if (nl < 0)
                    {
                        if (!eof && n < MaxBytes)
                        {
                            var got = fs.Read(buf, n, Math.Min(Chunk, MaxBytes - n));
                            if (got == 0) eof = true;
                            n += got;
                            continue;
                        }
                        // No newline left: the rest is a final line only if the whole file has been read; past the
                        // cap the line is truncated and skipped.
                        if (eof || fs.ReadByte() < 0) Parse(buf.AsMemory(start, n - start), ref cwd, ref title);
                        break;
                    }
                    Parse(buf.AsMemory(start, nl - start), ref cwd, ref title);
                    start = nl + 1;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            return new TranscriptInfo(cwd, title, lastWrite);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Last write time of the transcript without reading it; null when it does not exist.</summary>
    public DateTime? LastWriteUtc(string claudeSessionId)
    {
        try
        {
            if (Locate(claudeSessionId) is not { } file) return null;
            var info = new FileInfo(file);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The transcript path for a session id (cached once found), or null.</summary>
    private string? Locate(string claudeSessionId)
    {
        if (string.IsNullOrEmpty(claudeSessionId)) return null;
        // Session ids come from process environments; refuse anything that could escape the directory.
        if (claudeSessionId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (_paths.TryGetValue(claudeSessionId, out var cached))
        {
            if (File.Exists(cached)) return cached;
            _paths.Remove(claudeSessionId);
        }
        if (!Directory.Exists(_projectsDir)) return null;

        foreach (var dir in Directory.EnumerateDirectories(_projectsDir))
        {
            var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
            if (!File.Exists(candidate)) continue;
            if (_paths.Count >= MaxCachedPaths) _paths.Clear();
            _paths[claudeSessionId] = candidate;
            return candidate;
        }
        return null;
    }

    private static void Parse(ReadOnlyMemory<byte> line, ref string? cwd, ref string? title)
    {
        if (line.IsEmpty) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (cwd is null && root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String)
                cwd = SessionText.Clean(c.GetString(), MaxPath);
            title ??= TitleOf(root);
        }
        catch (JsonException) { }
    }

    /// <summary>
    /// The text of a user record typed by the user. Records Claude Code injects are skipped: those marked
    /// <c>"isMeta": true</c> and those whose text is only tags such as &lt;system-reminder&gt;...&lt;/system-reminder&gt;
    /// or &lt;command-name&gt;...&lt;/command-name&gt;. Leading tag blocks before the user's own words (a worktree or forked
    /// session's first prompt starts with a system reminder) are stripped. Text items of array content are joined.
    /// </summary>
    private static string? TitleOf(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "user") return null;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("content", out var content)) return null;

        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            List<string>? parts = null;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                    && item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    (parts ??= []).Add(tx.GetString()!);
            }
            if (parts is not null) text = string.Join("\n", parts);
        }
        if (text is null || StripLeadingTags(text) is not { } own) return null;

        // One line, no control or bidi characters (terminal escapes), at most MaxTitle characters.
        return SessionText.Clean(own, MaxTitle);
    }

    /// <summary>
    /// The text after any leading <c>&lt;tag ...&gt;...&lt;/tag&gt;</c> blocks; null when nothing but tags remains, or
    /// when the text starts with '&lt;' that does not open a closed tag block (so injected text is never a title).
    /// </summary>
    public static string? StripLeadingTags(string text)
    {
        var s = text.AsSpan().TrimStart();
        while (s.Length > 0 && s[0] == '<')
        {
            var i = 1;
            if (i >= s.Length || !char.IsAsciiLetter(s[i])) return null;
            while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '-' or '_' or ':' or '.')) i++;
            if (i >= s.Length || (s[i] != '>' && !char.IsWhiteSpace(s[i]))) return null;
            var close = string.Concat("</", s[1..i], ">");
            var end = s.IndexOf(close, StringComparison.Ordinal);
            if (end < 0) return null;
            s = s[(end + close.Length)..].TrimStart();
        }
        return s.Length == 0 ? null : s.ToString();
    }
}
