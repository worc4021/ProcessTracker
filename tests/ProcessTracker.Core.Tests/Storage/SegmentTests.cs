using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Storage;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Tests.Storage;

public sealed class SegmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-seg-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Far = T0.AddDays(1);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static ProcessSample Sample(int pid, DateTime ts, float cpu = 0, long ctx = 0) =>
        new(new ProcessKey(pid, 1000 + pid), ts, cpu, 111, 222, 0, 0, 0, 4, 5, 6, ctx, 7, 8);

    private static SystemSample Sys(DateTime ts) => new(ts, 12.5f, 2.5f, 8, 100, 200, 300, 400);

    private static TickFrame Frame(DateTime ts, IReadOnlyList<ProcessSample>? samples = null,
        IReadOnlyList<SessionAggregate>? sessions = null) =>
        new(ts, Sys(ts), samples ?? Array.Empty<ProcessSample>(), sessions ?? Array.Empty<SessionAggregate>());

    private static ProcessRecord Info(int pid, DateTime firstSeen) => new(
        new ProcessKey(pid, 1000 + pid), new ProcessKey(1, 5), "node.exe", null, "node é.js",
        new Attr(AttributionKind.Session, "unit-1", null, true), firstSeen, null);

    private async Task Write(Action<SegmentWriter> body)
    {
        using var w = new SegmentWriter(_dir, TimeSpan.FromHours(24), NullLogger.Instance);
        body(w);
        await w.FlushAsync();
    }

    private List<SegmentRecord> ReadAll() => SegmentReader.Read(_dir, DateTime.MinValue, Far).ToList();

    private string OnlyFile() => Assert.Single(Directory.GetFiles(_dir, "*.pts"));

    [Fact]
    public async Task Segment_RoundTripsAllRecordTypes()
    {
        var ts = T0.AddSeconds(4);
        var sample = Sample(10, ts, cpu: 1.5f, ctx: 9);
        var agg = new SessionAggregate("unit-1", ts, 1.5f, 111, 4, 5, 1);
        var info = Info(10, ts);
        await Write(w => w.Enqueue(Frame(ts, [sample], [agg]), [info]));

        var recs = ReadAll();
        Assert.Contains(new SystemRec(Sys(ts)), recs);
        Assert.Contains(new ProcessRec(sample), recs);
        Assert.Contains(new SessionRec(agg), recs);
        var pi = Assert.Single(recs.OfType<ProcessInfoRec>()).Value;
        Assert.Equal(info.Key, pi.Key);
        Assert.Equal(info.Parent, pi.Parent);
        Assert.Equal("node.exe", pi.ImageName);
        Assert.Null(pi.ImagePath);
        Assert.Equal("node é.js", pi.CommandLine);
        Assert.Equal(info.Attribution, pi.Attribution);
        Assert.Equal(ts, pi.FirstSeenUtc);
        Assert.Null(pi.ExitedUtc);
        Assert.Equal(DateTimeKind.Utc, pi.FirstSeenUtc.Kind);
    }

    [Fact]
    public async Task Segment_RollsFileEvery5Minutes()
    {
        await Write(w =>
        {
            w.Enqueue(Frame(new DateTime(2026, 1, 1, 12, 4, 58, DateTimeKind.Utc)), []);
            w.Enqueue(Frame(new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc)), []);
        });
        var names = Directory.GetFiles(_dir, "*.pts").Select(p => Path.GetFileName(p)).Order().ToArray();
        Assert.Equal(["20260101-1200.pts", "20260101-1205.pts"], names);
    }

    [Fact]
    public async Task Reader_TruncatedTail_ReturnsCompleteRecordsOnly()
    {
        await Write(w => w.Enqueue(Frame(T0.AddSeconds(2), [Sample(10, T0.AddSeconds(2), 1)]), []));
        var before = ReadAll();
        var file = OnlyFile();
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..^3]);

        var after = ReadAll();
        Assert.Equal(before.Count - 1, after.Count);
        Assert.Equal(before.Take(after.Count), after);
    }

    [Fact]
    public async Task Reader_UnknownRecordType_Skipped()
    {
        await Write(w => w.Enqueue(Frame(T0.AddSeconds(2)), []));
        var file = OnlyFile();
        var bytes = File.ReadAllBytes(file).ToList();
        bytes.AddRange([99, 3, 0, 1, 2, 3]); // unknown type, 3 byte payload
        File.WriteAllBytes(file, bytes.ToArray());
        // a known record after the unknown one must still be found
        await Write(w => w.Enqueue(Frame(T0.AddSeconds(4)), []));

        var recs = ReadAll();
        Assert.Equal(2, recs.OfType<SystemRec>().Count());
    }

    [Fact]
    public async Task Reader_LongerPayloadThanKnown_SkipsExtraBytes()
    {
        await Write(w => w.Enqueue(Frame(T0.AddSeconds(2)), []));
        var file = OnlyFile();
        var bytes = File.ReadAllBytes(file).ToList();
        // header is 6 bytes; the only record follows as [type][u16 len][payload] and is the file's tail
        var len = BitConverter.ToUInt16(bytes.ToArray(), 7);
        bytes.AddRange(new byte[8]);
        var newLen = (ushort)(len + 8);
        bytes[7] = (byte)(newLen & 0xFF);
        bytes[8] = (byte)(newLen >> 8);
        File.WriteAllBytes(file, bytes.ToArray());
        await Write(w => w.Enqueue(Frame(T0.AddSeconds(4)), []));

        var recs = ReadAll();
        Assert.Equal(2, recs.OfType<SystemRec>().Count());
        Assert.Equal(Sys(T0.AddSeconds(2)), recs.OfType<SystemRec>().First().Value);
    }

    [Fact]
    public async Task Writer_IdleProcess_WritesHeartbeatEvery60s()
    {
        await Write(w =>
        {
            for (var i = 0; i < 30; i++)
            {
                var ts = T0.AddSeconds(2 * i);
                w.Enqueue(Frame(ts, [Sample(10, ts)]), []);
            }
        });
        Assert.Single(ReadAll().OfType<ProcessRec>());
    }

    [Fact]
    public async Task Writer_ActiveProcess_WritesEveryTick()
    {
        await Write(w =>
        {
            for (var i = 0; i < 5; i++)
            {
                var ts = T0.AddSeconds(2 * i);
                w.Enqueue(Frame(ts, [Sample(10, ts, cpu: i == 0 ? 0 : 0.1f, ctx: i == 0 ? 1 : 0)]), []);
            }
        });
        Assert.Equal(5, ReadAll().OfType<ProcessRec>().Count());
    }

    [Fact]
    public async Task Reader_FiltersByWindow_AndSkipsFilesByName()
    {
        await Write(w =>
        {
            w.Enqueue(Frame(T0.AddSeconds(2)), []);
            w.Enqueue(Frame(T0.AddMinutes(6)), []);
            w.Enqueue(Frame(T0.AddMinutes(12)), []);
        });
        var recs = SegmentReader.Read(_dir, T0.AddMinutes(5), T0.AddMinutes(10)).ToList();
        Assert.Equal(T0.AddMinutes(6), Assert.Single(recs.OfType<SystemRec>()).Value.TimestampUtc);
    }

    [Fact]
    public void Retention_DeletesOldFiles_ClampsTo2h()
    {
        Directory.CreateDirectory(_dir);
        var now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        string F(DateTime start)
        {
            var p = Path.Combine(_dir, start.ToString("yyyyMMdd-HHmm") + ".pts");
            File.WriteAllBytes(p, [1]);
            return p;
        }
        var old = F(now.AddHours(-2).AddMinutes(-10));    // ended 2h05m ago: deleted
        var inside = F(now.AddHours(-1).AddMinutes(-30)); // 1.5h old: kept because retention clamps to 2h
        using var w = new SegmentWriter(_dir, TimeSpan.FromMinutes(1), NullLogger.Instance);
        w.EnforceRetention(now);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(inside));
    }

    [Fact]
    public async Task Writer_DirectoryUnwritable_LogsAndDoesNotThrow()
    {
        var filePath = Path.Combine(Path.GetTempPath(), "pt-notadir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "x");
        try
        {
            var log = new CapturingLogger();
            using var w = new SegmentWriter(filePath, TimeSpan.FromHours(24), log);
            w.Enqueue(Frame(T0), []);
            w.Enqueue(Frame(T0.AddSeconds(2)), []);
            await w.FlushAsync();
            Assert.Single(log.Messages);
        }
        finally { File.Delete(filePath); }
    }
}
