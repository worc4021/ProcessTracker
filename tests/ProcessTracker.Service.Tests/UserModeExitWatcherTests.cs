using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessTracker.Native;

namespace ProcessTracker.Service.Tests;

/// <summary>Uses a test-specific event name, never the real exit event (ruling R1).</summary>
public class UserModeExitWatcherTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    private static string TestEventName() => $@"Global\ProcessTracker.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Signal_StopsTheApplication()
    {
        var name = TestEventName();
        using var exitEvent = UserModeSignal.CreateForInstance(name);
        var lifetime = new FakeLifetime();
        using var watcher = new UserModeExitWatcher(exitEvent, lifetime, NullLogger<UserModeExitWatcher>.Instance);
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(UserModeSignal.TrySignal(name));

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await watcher.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HostStopping_WithoutSignal_DoesNotStopTheApplication()
    {
        using var exitEvent = UserModeSignal.CreateForInstance(TestEventName());
        var lifetime = new FakeLifetime();
        using var watcher = new UserModeExitWatcher(exitEvent, lifetime, NullLogger<UserModeExitWatcher>.Instance);
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        await watcher.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(lifetime.Stopped.Task.IsCompleted);
    }
}
