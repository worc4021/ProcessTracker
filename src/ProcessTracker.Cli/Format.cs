using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcessTracker.Core.Contracts;

namespace ProcessTracker.Cli;

/// <summary>Plain fixed-width table rendering and value formatting (no colour, invariant culture).</summary>
public static class Format
{
    private const double Gb = 1024.0 * 1024 * 1024;

    public static string Cores(float cores) => cores.ToString("F2", CultureInfo.InvariantCulture);
    public static string Gigabytes(long bytes) => (bytes / Gb).ToString("F2", CultureInfo.InvariantCulture);
    public static string Gigabytes(double gb) => gb.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>I/O bytes of one sampling interval as MB with 2 decimals.</summary>
    public static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Idle time as e.g. "12m", "3h", "2d"; "-" when unknown.</summary>
    public static string Idle(DateTime? lastActiveUtc, DateTime nowUtc)
    {
        if (lastActiveUtc is not { } t) return "-";
        var span = nowUtc - t;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    public static string Truncate(string? s, int width)
    {
        s ??= "";
        s = s.ReplaceLineEndings(" ");
        return s.Length <= width ? s : string.Concat(s.AsSpan(0, width - 1), "~");
    }

    /// <summary>Renders rows under headers; columns whose header is in <paramref name="rightAligned"/> are right-aligned.</summary>
    public static string Table(string[] headers, IReadOnlyList<string[]> rows, params string[] rightAligned)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        void Line(string[] cells)
        {
            var line = new StringBuilder();
            for (var i = 0; i < cells.Length; i++)
            {
                if (i > 0) line.Append("  ");
                line.Append(rightAligned.Contains(headers[i]) ? cells[i].PadLeft(widths[i]) : cells[i].PadRight(widths[i]));
            }
            sb.AppendLine(line.ToString().TrimEnd());
        }
        Line(headers);
        foreach (var r in rows) Line(r);
        return sb.ToString();
    }

    /// <summary>The console's width in columns; null when output is redirected or the width is unknown.</summary>
    public static int? ConsoleWidth()
    {
        try { return Console.IsOutputRedirected || Console.WindowWidth <= 0 ? null : Console.WindowWidth; }
        catch (IOException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    /// <summary>The length of the longest line of rendered text.</summary>
    public static int MaxLineLength(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r').Length).DefaultIfEmpty(0).Max();

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);

    public static string[] ProcessRow(ProcessViewDto p, DateTime nowUtc) =>
    [
        p.Pid.ToString(CultureInfo.InvariantCulture),
        Truncate(p.ImageName, 28),
        Truncate(p.Attribution.UnitId ?? p.Attribution.Kind.ToString(), 24),
        Cores(p.CpuCores),
        Gigabytes(p.PrivateBytes),
        Megabytes(p.ReadBytesDelta + p.WriteBytesDelta + p.OtherBytesDelta),
        Idle(p.EffectiveLastActiveUtc ?? p.LastActiveUtc, nowUtc),
        p.State.ToString() + (p.Alertable ? "!" : ""),
    ];

    public static readonly string[] ProcessHeaders = ["PID", "IMAGE", "UNIT", "CPU", "MEM_GB", "IO_MB", "IDLE", "STATE"];
    public static readonly string[] ProcessRight = ["PID", "CPU", "MEM_GB", "IO_MB", "IDLE"];
}
