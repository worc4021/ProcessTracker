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
    public void DefaultMaxSize_Is5MB() => Assert.Equal(5L * 1024 * 1024, FileLoggerProvider.DefaultMaxBytes);
}
