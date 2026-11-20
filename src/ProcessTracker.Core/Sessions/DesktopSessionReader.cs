using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcessTracker.Core.Sessions;

/// <summary>
/// What the Claude desktop app records about one of its Claude Code sessions: the sidebar title, git branch, worktree,
/// folders and a summary of the last turn. Every string is cleaned (one line, no control characters) and capped.
/// </summary>
public sealed record DesktopSessionInfo(
    string? Title,
    string? Branch,
    string? WorktreeName,
    string? Cwd,
    string? OriginCwd,
    string? Summary,
    DateTime? LastFocusedUtc);

/// <summary>
/// Reads the desktop app's per-session metadata,
/// <c>&lt;profile&gt;\AppData\Local\Claude-Data\claude_*\claude-code-sessions\&lt;account&gt;\&lt;org&gt;\&lt;hostId&gt;.json</c>.
/// The service runs as SYSTEM over a tree the user controls, so only those fixed levels are enumerated, no reparse
/// point (junction, symbolic link) is followed at any level below the profile, the host id must match a strict
/// pattern before it is used in a path, and at most 1 MB is read. Never throws. Single-threaded use.
/// </summary>
public sealed partial class DesktopSessionReader
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxTitle = 120;
    public const int MaxName = 120;
    public const int MaxSummary = 300;
    public const int MaxPath = 400;
    private const int MaxCached = 1024;

    private static readonly EnumerationOptions DirsOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
        ReturnSpecialDirectories = false,
    };

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    private readonly string _profile;
    private readonly Dictionary<string, (string Path, DateTime Written, DesktopSessionInfo Info)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public DesktopSessionReader(string userProfilePath) => _profile = Path.GetFullPath(userProfilePath);

    [GeneratedRegex(@"\Alocal_[0-9a-fA-F-]{36}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostIdPattern();

    /// <summary>True when <paramref name="hostSessionId"/> has the shape the desktop app uses (<c>local_&lt;guid&gt;</c>).</summary>
    public static bool IsValidHostId(string? hostSessionId) =>
        !string.IsNullOrEmpty(hostSessionId) && HostIdPattern().IsMatch(hostSessionId);

    /// <summary>
    /// The metadata of a desktop session, or null when the id is malformed, the file is missing, unsafe, too large or
    /// not a JSON object. Re-parsed only when the file's last write time changes.
    /// </summary>
    public DesktopSessionInfo? Read(string? hostSessionId)
    {
        try
        {
            if (!IsValidHostId(hostSessionId)) return null;
            var id = hostSessionId!;
            if (Locate(id) is not { } file) return null;

            var fi = new FileInfo(file);
            if (!fi.Exists || IsReparse(fi.Attributes) || fi.Length > MaxBytes) return null;
            var written = fi.LastWriteTimeUtc;
            if (_cache.TryGetValue(id, out var c) && c.Path == file && c.Written == written) return c.Info;

            if (ReadCapped(file) is not { } bytes) return null;
            if (Parse(bytes) is not { } info) return null;
            if (_cache.Count >= MaxCached) _cache.Clear();
            _cache[id] = (file, written, info);
            return info;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException))
        {
            return null;
        }
    }

    /// <summary>The metadata file of the session (cached path re-checked every time), or null.</summary>
    private string? Locate(string id)
    {
        if (_cache.TryGetValue(id, out var c))
        {
            if (IsSafeFile(c.Path)) return c.Path;
            _cache.Remove(id);
        }

        var root = Path.Combine(_profile, "AppData", "Local", "Claude-Data");
        if (!IsSafeDir(Path.Combine(_profile, "AppData")) || !IsSafeDir(Path.Combine(_profile, "AppData", "Local"))
            || !IsSafeDir(root)) return null;

        var name = id + ".json";
        foreach (var instance in new DirectoryInfo(root).EnumerateDirectories("claude_*", DirsOnly))
        {
            var sessions = Path.Combine(instance.FullName, "claude-code-sessions");
            if (!IsSafeDir(sessions)) continue;
            foreach (var account in new DirectoryInfo(sessions).EnumerateDirectories("*", DirsOnly))
            foreach (var org in account.EnumerateDirectories("*", DirsOnly))
            {
                var candidate = Path.Combine(org.FullName, name);
                if (IsSafeFile(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>The file exists, and neither it nor any directory between it and the profile is a reparse point.</summary>
    private bool IsSafeFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || IsReparse(fi.Attributes)) return false;
        for (var dir = fi.Directory; dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.FullName.TrimEnd('\\'), _profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            if (!dir.Exists || IsReparse(dir.Attributes)) return false;
        }
        return false; // not under the profile
    }

    private static bool IsSafeDir(string path)
    {
        var di = new DirectoryInfo(path);
        return di.Exists && !IsReparse(di.Attributes);
    }

    private static bool IsReparse(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0;

    /// <summary>The whole file if it is at most <see cref="MaxBytes"/>; null when it is larger.</summary>
    private static byte[]? ReadCapped(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan);
        if (fs.Length > MaxBytes) return null;
        var buf = new byte[(int)fs.Length];
        var n = 0;
        while (n < buf.Length)
        {
            var got = fs.Read(buf, n, buf.Length - n);
            if (got == 0) break;
            n += got;
        }
        return n == buf.Length ? buf : buf[..n];
    }

    private static DesktopSessionInfo? Parse(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, JsonOptions);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            return new DesktopSessionInfo(
                Clean(Str(r, "title"), MaxTitle),
                Clean(Str(r, "branch"), MaxName),
                Clean(Str(r, "worktreeName"), MaxName),
                Clean(Str(r, "cwd"), MaxPath),
                Clean(Str(r, "originCwd"), MaxPath),
                Clean(Str(r, "postTurnSummary"), MaxSummary),
                EpochMs(r, "lastFocusedAt"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? EpochMs(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var ms))
            return null;
        // DateTimeOffset's valid range in Unix milliseconds.
        if (ms < -62135596800000L || ms > 253402300799999L) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    /// <summary>
    /// One line of at most <paramref name="max"/> characters: whitespace and control characters collapse to single
    /// spaces, bidirectional overrides are dropped, a trailing surrogate pair is never split. Null when empty.
    /// </summary>
    public static string? Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
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
        {
            var len = char.IsHighSurrogate(sb[max - 1]) ? max - 1 : max;
            sb.Length = len;
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
}
