using System.Text.Json;

namespace ProcessTracker.Core.Sessions;

public sealed record TranscriptInfo(string? Cwd, string? Title, DateTime LastWriteUtc);

/// <summary>Reads cwd and a title from a Claude Code transcript (<c>.claude\projects\*\&lt;id&gt;.jsonl</c>).</summary>
public sealed class TranscriptReader
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxTitle = 80;
    private readonly string _projectsDir;

    public TranscriptReader(string userProfilePath) =>
        _projectsDir = Path.Combine(userProfilePath, ".claude", "projects");

    public TranscriptInfo? Read(string claudeSessionId)
    {
        if (string.IsNullOrEmpty(claudeSessionId) || !Directory.Exists(_projectsDir)) return null;
        // Session ids come from process environments; refuse anything that could escape the directory.
        if (claudeSessionId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        try
        {
            string? file = null;
            foreach (var dir in Directory.EnumerateDirectories(_projectsDir))
            {
                var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
                if (File.Exists(candidate)) { file = candidate; break; }
            }
            if (file is null) return null;

            var lastWrite = File.GetLastWriteTimeUtc(file);
            string? cwd = null, title = null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[MaxBytes];
            var n = 0;
            int got;
            while (n < MaxBytes && (got = fs.Read(buf, n, MaxBytes - n)) > 0) n += got;
            // If the cap was hit, the last line may be truncated: only parse complete lines.
            var end = n;
            if (n == MaxBytes && fs.ReadByte() >= 0)
            {
                end = Array.LastIndexOf(buf, (byte)'\n', n - 1);
                if (end < 0) end = 0;
            }

            var start = 0;
            while (start < end && (cwd is null || title is null))
            {
                var nl = Array.IndexOf(buf, (byte)'\n', start, end - start);
                var stop = nl < 0 ? end : nl;
                if (stop > start)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, start, stop - start));
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (cwd is null && root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(c.GetString()))
                                cwd = c.GetString();
                            title ??= TitleOf(root);
                        }
                    }
                    catch (JsonException) { }
                }
                start = stop + 1;
            }
            return new TranscriptInfo(cwd, title, lastWrite);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? TitleOf(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "user") return null;
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("content", out var content)) return null;

        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
        {
            var first = content[0];
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                && first.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                text = tx.GetString();
        }
        if (text is null) return null;

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return null;
        if (oneLine.Length <= MaxTitle) return oneLine;
        var len = char.IsHighSurrogate(oneLine[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle;
        return oneLine[..len];
    }
}
