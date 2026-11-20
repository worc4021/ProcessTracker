using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ProcessTracker.Core.Model;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Storage;

/// <summary>
/// On-disk segment format (version 1). Header: ASCII "PTSG" + u16 version. Then records
/// [u8 type][u16 payloadLength][payload], little-endian. Strings are u16 length + UTF-8 (0xFFFF = null).
/// Additive evolution only: readers read the fields they know, skip the rest of a payload and skip unknown types.
/// </summary>
public static class SegmentFormat
{
    public const ushort Version = 1;
    public const int HeaderLength = 6;
    public const int RecordHeaderLength = 3;
    public const byte TypeSystem = 1, TypeProcess = 2, TypeProcessInfo = 3, TypeSession = 4;
    public static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(5);
    public const string Extension = ".pts";
    private const string NameFormat = "yyyyMMdd-HHmm";
    private const ushort NullString = 0xFFFF;

    private static ReadOnlySpan<byte> Magic => "PTSG"u8;

    // Strings are capped (in chars) so a record can never exceed the u16 payload length.
    private const int MaxImageName = 512, MaxImagePath = 2048, MaxCommandLine = 8192, MaxId = 256;

    public static DateTime FloorToSegment(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % SegmentLength.Ticks, DateTimeKind.Utc);

    public static string FileName(DateTime segmentStartUtc) =>
        segmentStartUtc.ToString(NameFormat, CultureInfo.InvariantCulture) + Extension;

    public static bool TryParseFileName(string path, out DateTime startUtc)
    {
        startUtc = default;
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)) return false;
        return DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), NameFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startUtc);
    }

    public static byte[] Header()
    {
        var h = new byte[HeaderLength];
        Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), Version);
        return h;
    }

    public static bool IsValidHeader(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= HeaderLength && bytes[..4].SequenceEqual(Magic);

    /// <summary>Length of the longest prefix of whole records (header included); 0 if the header is invalid.</summary>
    public static int ValidLength(ReadOnlySpan<byte> bytes)
    {
        if (!IsValidHeader(bytes)) return 0;
        var pos = HeaderLength;
        while (pos + RecordHeaderLength <= bytes.Length)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pos + 1)..]);
            if (pos + RecordHeaderLength + len > bytes.Length) break;
            pos += RecordHeaderLength + len;
        }
        return pos;
    }

    // ---- encoding ----

    public static void WriteRecord(Stream s, byte type, Action<BinaryWriter> payload)
    {
        var ms = new MemoryStream(128);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) payload(bw);
        var buf = ms.GetBuffer();
        var len = (int)ms.Length;
        if (len > ushort.MaxValue) throw new InvalidOperationException("Record payload too large.");
        Span<byte> head = stackalloc byte[RecordHeaderLength];
        head[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)len);
        s.Write(head);
        s.Write(buf, 0, len);
    }

    private static void Ticks(BinaryWriter w, DateTime t) => w.Write(t.Ticks);

    private static void Str(BinaryWriter w, string? s, int maxChars)
    {
        if (s is null) { w.Write(NullString); return; }
        if (s.Length > maxChars) s = s[..maxChars];
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    public static void WriteSystem(BinaryWriter w, SystemSample s)
    {
        Ticks(w, s.TimestampUtc);
        w.Write(s.CpuBusyPercent); w.Write(s.BusyCores); w.Write(s.LogicalCpuCount);
        w.Write(s.CommitChargeBytes); w.Write(s.CommitLimitBytes);
        w.Write(s.AvailablePhysicalBytes); w.Write(s.TotalPhysicalBytes);
    }

    public static void WriteProcess(BinaryWriter w, ProcessSample p)
    {
        w.Write(p.Key.Pid); w.Write(p.Key.CreateTimeTicks); Ticks(w, p.TimestampUtc);
        w.Write(p.CpuCores); w.Write(p.PrivateBytes); w.Write(p.WorkingSet);
        w.Write(p.ReadOpsDelta); w.Write(p.WriteOpsDelta); w.Write(p.OtherOpsDelta);
        w.Write(p.ReadBytesDelta); w.Write(p.WriteBytesDelta); w.Write(p.OtherBytesDelta);
        w.Write(p.ContextSwitchesDelta); w.Write(p.HandleCount); w.Write(p.ThreadCount);
    }

    public static void WriteProcessInfo(BinaryWriter w, ProcessRecord r)
    {
        w.Write(r.Key.Pid); w.Write(r.Key.CreateTimeTicks);
        w.Write(r.Parent is not null);
        w.Write(r.Parent?.Pid ?? 0); w.Write(r.Parent?.CreateTimeTicks ?? 0L);
        Str(w, r.ImageName, MaxImageName); Str(w, r.ImagePath, MaxImagePath); Str(w, r.CommandLine, MaxCommandLine);
        w.Write((byte)r.Attribution.Kind);
        Str(w, r.Attribution.UnitId, MaxId); Str(w, r.Attribution.ClaudeSessionId, MaxId);
        w.Write(r.Attribution.IsAnchor);
        Ticks(w, r.FirstSeenUtc);
        w.Write(r.ExitedUtc is not null);
        w.Write(r.ExitedUtc?.Ticks ?? 0L);
    }

    public static void WriteSession(BinaryWriter w, SessionAggregate a)
    {
        Str(w, a.UnitId, MaxId); Ticks(w, a.TimestampUtc);
        w.Write(a.CpuCores); w.Write(a.PrivateBytes); w.Write(a.ReadBytesDelta); w.Write(a.WriteBytesDelta);
        w.Write(a.ProcessCount);
    }

    // ---- decoding (each returns null when the payload is shorter than the fields we know) ----

    public static SegmentRecord? Decode(byte type, ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        try
        {
            return type switch
            {
                TypeSystem => new SystemRec(new SystemSample(r.Utc(), r.F32(), r.F32(), r.I32(),
                    r.I64(), r.I64(), r.I64(), r.I64())),
                TypeProcess => new ProcessRec(new ProcessSample(new ProcessKey(r.I32(), r.I64()), r.Utc(), r.F32(),
                    r.I64(), r.I64(), r.I64(), r.I64(), r.I64(), r.I64(), r.I64(), r.I64(), r.I64(), r.I32(), r.I32())),
                TypeProcessInfo => new ProcessInfoRec(ReadInfo(ref r)),
                TypeSession => new SessionRec(new SessionAggregate(r.Str() ?? "", r.Utc(), r.F32(),
                    r.I64(), r.I64(), r.I64(), r.I32())),
                _ => null,
            };
        }
        catch (EndOfPayloadException)
        {
            return null;
        }
    }

    private static ProcessRecord ReadInfo(ref PayloadReader r)
    {
        var key = new ProcessKey(r.I32(), r.I64());
        var hasParent = r.Bool();
        var parent = new ProcessKey(r.I32(), r.I64());
        var image = r.Str() ?? "";
        var path = r.Str();
        var cmd = r.Str();
        var kind = (AttributionKind)r.U8();
        var attr = new Attr(kind, r.Str(), r.Str(), r.Bool());
        var first = r.Utc();
        var hasExit = r.Bool();
        var exit = r.I64();
        return new ProcessRecord(key, hasParent ? parent : null, image, path, cmd, attr, first,
            hasExit ? new DateTime(exit, DateTimeKind.Utc) : null);
    }

    private sealed class EndOfPayloadException : Exception;

    private ref struct PayloadReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (_pos + n > _data.Length) throw new EndOfPayloadException();
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public byte U8() => Take(1)[0];
        public bool Bool() => U8() != 0;
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public DateTime Utc() => new(Math.Clamp(I64(), DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);

        public string? Str()
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return len == NullString ? null : Encoding.UTF8.GetString(Take(len));
        }
    }
}
