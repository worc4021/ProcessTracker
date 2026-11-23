using ProcessTracker.Service.Logging;

namespace ProcessTracker.Service.Tests;

/// <summary>Uses test-specific object names, never the real exit event (ruling R1).</summary>
public sealed class UserModeExitEventTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProcessTracker-exitevent-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string Log()
    {
        var path = Path.Combine(_dir, FileLoggerProvider.FileName);
        if (!File.Exists(path)) return "";
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    [Fact]
    public void TryCreate_NameHeldByAnotherObjectType_ReturnsNullAndLogs()
    {
        var name = $@"Local\ProcessTracker.Test.{Guid.NewGuid():N}";
        using var squatter = new Mutex(initiallyOwned: false, name); // a different kernel object type under the name

        EventWaitHandle? handle;
        using (var provider = new FileLoggerProvider(_dir))
            handle = UserModeExitEvent.TryCreate(provider.CreateLogger("ProcessTracker"), name);

        Assert.Null(handle);
        Assert.Contains("Cannot create the user-mode exit event", Log());
        Assert.Contains(name, Log());
    }

    [Fact]
    public void TryCreate_FreeName_ReturnsTheEvent()
    {
        var name = $@"Local\ProcessTracker.Test.{Guid.NewGuid():N}";
        using var provider = new FileLoggerProvider(_dir);

        using var handle = UserModeExitEvent.TryCreate(provider.CreateLogger("ProcessTracker"), name);

        Assert.NotNull(handle);
        Assert.False(handle.WaitOne(0));
    }
}
