using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Storage;

/// <summary>
/// Appends tick frames to 5-minute segment files from a single background task. Enqueue never blocks
/// (bounded channel, drop-oldest). Idle processes are written only as a 60 s heartbeat.
/// </summary>
public sealed class SegmentWriter : IDisposable
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinRetention = TimeSpan.FromHours(2);

    private readonly record struct Item(TickFrame Frame, IReadOnlyList<ProcessRecord> NewProcesses);
    private struct Seen { public DateTime LastWritten; public DateTime LastSeen; }

    private readonly string _dir;
    private readonly TimeSpan _retention;
    private readonly ILogger _log;
    private readonly Channel<Item> _channel;
    private readonly Task _drain;
    private readonly Dictionary<ProcessKey, Seen> _seen = new();
    private readonly HashSet<string> _loggedFailures = new();
    private long _enqueued;
    private long _completed;
    private FileStream? _stream;
    private DateTime _segmentStart;
    private DateTime _lastPrune;

    public SegmentWriter(string dir, TimeSpan retention, ILogger log)
    {
        _dir = dir;
        _retention = retention < MinRetention ? MinRetention : retention;
        _log = log;
        _channel = Channel.CreateBounded(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            (Item _) => Interlocked.Increment(ref _completed));
        _drain = Task.Run(DrainAsync);
    }

    public void Enqueue(TickFrame f, IReadOnlyList<ProcessRecord> newProcesses)
    {
        Interlocked.Increment(ref _enqueued);
        if (!_channel.Writer.TryWrite(new Item(f, newProcesses)))
            Interlocked.Increment(ref _completed); // writer completed (disposed): frame discarded
    }

    /// <summary>Completes when every frame enqueued before the call has been written (or dropped). For tests.</summary>
    public async Task FlushAsync()
    {
        var target = Interlocked.Read(ref _enqueued);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Interlocked.Read(ref _completed) < target && DateTime.UtcNow < deadline)
            await Task.Delay(2);
    }

    /// <summary>Deletes segment files that ended before now - retention (retention is at least 2 h).</summary>
    public void EnforceRetention(DateTime nowUtc)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = nowUtc - _retention;
            foreach (var path in Directory.EnumerateFiles(_dir, "*" + SegmentFormat.Extension))
            {
                if (!SegmentFormat.TryParseFileName(path, out var start)) continue;
                if (start + SegmentFormat.SegmentLength >= cutoff) continue;
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LogOnce(e); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogOnce(e);
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try { WriteFrame(item); }
            catch (Exception e)
            {
                CloseStream();
                LogOnce(e);
            }
            finally { Interlocked.Increment(ref _completed); }
        }
    }

    private void WriteFrame(Item item)
    {
        var f = item.Frame;
        var stream = OpenSegment(SegmentFormat.FloorToSegment(f.TimestampUtc));

        using var buf = new MemoryStream(4096);
        SegmentFormat.WriteRecord(buf, SegmentFormat.TypeSystem, w => SegmentFormat.WriteSystem(w, f.System));
        foreach (var info in item.NewProcesses)
            SegmentFormat.WriteRecord(buf, SegmentFormat.TypeProcessInfo, w => SegmentFormat.WriteProcessInfo(w, info));
        foreach (var s in f.Samples)
            if (ShouldWrite(s, f.TimestampUtc))
                SegmentFormat.WriteRecord(buf, SegmentFormat.TypeProcess, w => SegmentFormat.WriteProcess(w, s));
        foreach (var a in f.Sessions)
            SegmentFormat.WriteRecord(buf, SegmentFormat.TypeSession, w => SegmentFormat.WriteSession(w, a));
        PruneSeen(f.TimestampUtc);

        stream.Write(buf.GetBuffer(), 0, (int)buf.Length);
        stream.Flush(false);
    }

    private bool ShouldWrite(ProcessSample s, DateTime now)
    {
        var active = s.CpuCores > 0 || s.ReadOpsDelta > 0 || s.WriteOpsDelta > 0
                     || s.OtherOpsDelta > 0 || s.ContextSwitchesDelta > 0;
        var known = _seen.TryGetValue(s.Key, out var seen);
        var write = active || !known || now - seen.LastWritten >= Heartbeat;
        _seen[s.Key] = new Seen { LastWritten = write ? now : seen.LastWritten, LastSeen = now };
        return write;
    }

    private void PruneSeen(DateTime now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(1)) return;
        _lastPrune = now;
        List<ProcessKey>? stale = null;
        foreach (var (key, seen) in _seen)
            if (now - seen.LastSeen > ForgetAfter) (stale ??= new()).Add(key);
        if (stale is not null) foreach (var key in stale) _seen.Remove(key);
    }

    private FileStream OpenSegment(DateTime start)
    {
        if (_stream is not null && start == _segmentStart) return _stream;
        CloseStream();

        Directory.CreateDirectory(_dir);
        var fs = new FileStream(Path.Combine(_dir, SegmentFormat.FileName(start)), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            // Appending to an existing file (service restart inside the same 5 minutes): cut any torn tail first.
            var existing = new byte[fs.Length];
            fs.ReadExactly(existing);
            var valid = SegmentFormat.ValidLength(existing);
            if (valid != existing.Length) fs.SetLength(valid);
            fs.Seek(valid, SeekOrigin.Begin);
            if (valid == 0) fs.Write(SegmentFormat.Header());
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        _stream = fs;
        _segmentStart = start;
        return fs;
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        _stream = null;
    }

    private void LogOnce(Exception e)
    {
        var kind = e.GetType().Name;
        bool first;
        lock (_loggedFailures) first = _loggedFailures.Add(kind);
        if (first) _log.LogError(e, "History segment write failed ({Kind}); frames are dropped until it recovers", kind);
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _drain.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        CloseStream();
    }
}
