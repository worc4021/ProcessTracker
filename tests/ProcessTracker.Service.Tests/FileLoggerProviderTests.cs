using Microsoft.Extensions.Logging;
using ProcessTracker.Service.Logging;

namespace ProcessTracker.Service.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProcessTracker-log-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string Read(string name)
    {
        using var reader = new StreamReader(new FileStream(Path.Combine(_dir, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    [Fact]
    public void WritesLevelCategoryMessageAndException()
    {
        using (var provider = new FileLoggerProvider(_dir))
        {
            var logger = provider.CreateLogger("Cat.Egory");
            logger.LogWarning("hello {Name}", "world");
            logger.LogError(new InvalidOperationException("boom"), "failed");
        }

        var text = Read("processtracker.log");
        Assert.Contains("[Warning] Cat.Egory: hello world", text);
        Assert.Contains("[Error] Cat.Egory: failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void RollsAtMaxSize_KeepingTwoFiles()
    {
        const long max = 1000;
        using (var provider = new FileLoggerProvider(_dir, max))
        {
            var logger = provider.CreateLogger("c");
            for (var i = 0; i < 100; i++) logger.LogInformation("line {I:D3} {Pad}", i, new string('x', 40));
        }

        Assert.Equal(["processtracker.log", "processtracker.log.1"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(Directory.GetFiles(_dir), f => Assert.True(new FileInfo(f).Length <= max, f));
        Assert.Contains("line 099", Read("processtracker.log"));
        Assert.DoesNotContain("line 000", Read("processtracker.log") + Read("processtracker.log.1"));
    }

    [Fact]
    public void SecondWriterOnTheSameFile_BothWritersKeepLogging()
    {
        // A takeover or rollback process logs to the same file while the instance it replaces still holds it open.
        using (var first = new FileLoggerProvider(_dir))
        using (var second = new FileLoggerProvider(_dir))
        {
            first.CreateLogger("old").LogInformation("one");
            second.CreateLogger("new").LogInformation("two");
            first.CreateLogger("old").LogInformation("three");
            second.CreateLogger("new").LogInformation("four");
        }

        var text = Read("processtracker.log");
        foreach (var line in new[] { "old: one", "new: two", "old: three", "new: four" })
            Assert.Contains(line, text);
        Assert.Equal(4, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>Two providers on one folder stand in for two processes: each has its own lock and its own handle.</summary>
    private static void Hammer(FileLoggerProvider first, FileLoggerProvider second, int threads, int linesPerThread)
    {
        var writers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var logger = (t % 2 == 0 ? first : second).CreateLogger($"w{t}");
            for (var i = 0; i < linesPerThread; i++) logger.LogInformation("entry {T:D2}-{I:D4} end", t, i);
        })).ToList();
        writers.ForEach(w => w.Start());
        writers.ForEach(w => w.Join());
    }

    private static readonly System.Text.RegularExpressions.Regex Entry =
        new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[Information\] w(\d+): entry (\d\d-\d{4}) end$");

    [Fact]
    public void TwoWritersAppendingAtOnce_LoseAndGarbleNothing()
    {
        using (var first = new FileLoggerProvider(_dir))
        using (var second = new FileLoggerProvider(_dir))
            Hammer(first, second, threads: 8, linesPerThread: 300);

        var lines = Read("processtracker.log").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.All(lines, l => Assert.Matches(Entry, l));
        Assert.Equal(8 * 300, lines.Select(l => Entry.Match(l).Groups[2].Value).Distinct().Count());
    }

    [Fact]
    public void TwoWritersRollingOverAtOnce_GarbleNothingAndStayUnderTheLimit()
    {
        const long max = 4096;
        using (var first = new FileLoggerProvider(_dir, max))
        using (var second = new FileLoggerProvider(_dir, max))
            Hammer(first, second, threads: 8, linesPerThread: 100);

        Assert.All(Directory.GetFiles(_dir), f => Assert.True(new FileInfo(f).Length <= max, $"{f}: {new FileInfo(f).Length} bytes"));
        var lines = (Read("processtracker.log") + Read("processtracker.log.1"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.All(lines, l => Assert.Matches(Entry, l));
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public void RolloverBlocked_KeepsLoggingToTheCurrentFile()
    {
        // A viewer holds processtracker.log.1 open without FILE_SHARE_DELETE, so it can't be replaced.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "processtracker.log.1"), "old\n");
        using var viewer = new FileStream(Path.Combine(_dir, "processtracker.log.1"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using (var provider = new FileLoggerProvider(_dir, maxBytes: 500))
        {
            var logger = provider.CreateLogger("c");
            for (var i = 0; i < 20; i++) logger.LogInformation("line {I:D3} {Pad}", i, new string('x', 40));
        }

        var text = Read("processtracker.log");
        Assert.Contains("line 000", text);
        Assert.Contains("line 019", text); // over the limit, but nothing is lost while the rollover can't happen
    }

    [Fact]
    public void MutexNameTakenByAnotherKindOfObject_StillLogs()
    {
        // Something else squats the name (here an event): logging goes on without the cross-process ordering.
        Directory.CreateDirectory(_dir);
        using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset,
            FileLoggerProvider.MutexName(Path.Combine(_dir, FileLoggerProvider.FileName)));
        using (var provider = new FileLoggerProvider(_dir))
            provider.CreateLogger("c").LogInformation("still here");

        Assert.Contains("still here", Read("processtracker.log"));
    }

    [Fact]
    public void FileCanBeRenamedWhileOpen()
    {
        using var provider = new FileLoggerProvider(_dir);
        provider.CreateLogger("c").LogInformation("held");

        // Another process rolling the file over (rename) must not fail because this one holds it.
        File.Move(Path.Combine(_dir, "processtracker.log"), Path.Combine(_dir, "processtracker.log.1"), overwrite: true);

        Assert.Contains("held", Read("processtracker.log.1"));
    }

    [Fact]
    public void DefaultMaxSize_Is5MB() => Assert.Equal(5L * 1024 * 1024, FileLoggerProvider.DefaultMaxBytes);
}
