using System.Buffers.Binary;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Storage;

public abstract record SegmentRecord;
public sealed record SystemRec(SystemSample Value) : SegmentRecord;
public sealed record ProcessRec(ProcessSample Value) : SegmentRecord;
public sealed record ProcessInfoRec(ProcessRecord Value) : SegmentRecord;
public sealed record SessionRec(SessionAggregate Value) : SegmentRecord;

/// <summary>Reads segment files. Tolerates torn tails, unknown record types and longer payloads.</summary>
public static class SegmentReader
{
    /// <summary>
    /// Records whose timestamp is in [fromUtc, toUtc] (ProcessInfo by FirstSeenUtc), across files in name order.
    /// Files whose 5-minute span lies outside the window are skipped by name.
    /// </summary>
    public static IEnumerable<SegmentRecord> Read(string dir, DateTime fromUtc, DateTime toUtc)
    {
        if (!Directory.Exists(dir)) yield break;

        var files = new List<(DateTime Start, string Path)>();
        foreach (var path in Directory.EnumerateFiles(dir, "*" + SegmentFormat.Extension))
            if (SegmentFormat.TryParseFileName(path, out var start)) files.Add((start, path));
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a.Path), Path.GetFileName(b.Path)));

        foreach (var (start, path) in files)
        {
            if (start > toUtc || start + SegmentFormat.SegmentLength <= fromUtc) continue;
            var bytes = TryReadAll(path);
            if (bytes is null || !SegmentFormat.IsValidHeader(bytes)) continue;

            foreach (var rec in Parse(bytes))
            {
                var ts = rec switch
                {
                    SystemRec s => s.Value.TimestampUtc,
                    ProcessRec p => p.Value.TimestampUtc,
                    ProcessInfoRec i => i.Value.FirstSeenUtc,
                    SessionRec s => s.Value.TimestampUtc,
                    _ => default,
                };
                if (ts >= fromUtc && ts <= toUtc) yield return rec;
            }
        }
    }

    private static List<SegmentRecord> Parse(byte[] bytes)
    {
        var result = new List<SegmentRecord>();
        var pos = SegmentFormat.HeaderLength;
        while (pos + SegmentFormat.RecordHeaderLength <= bytes.Length)
        {
            var type = bytes[pos];
            var len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 1));
            pos += SegmentFormat.RecordHeaderLength;
            if (pos + len > bytes.Length) break; // torn tail
            var rec = SegmentFormat.Decode(type, bytes.AsSpan(pos, len));
            if (rec is not null) result.Add(rec);
            pos += len;
        }
        return result;
    }

    private static byte[]? TryReadAll(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            return buf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
