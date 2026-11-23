namespace ProcessTracker.Service;

/// <summary>
/// User mode only: waits on the instance's exit event (<see cref="ProcessTracker.Native.UserModeSignal"/>) and stops
/// the host when it is set, so the instance exits through the normal graceful shutdown and persists its state.
/// </summary>
public sealed class UserModeExitWatcher(
    EventWaitHandle exitEvent, IHostApplicationLifetime lifetime, ILogger<UserModeExitWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => signalled.TrySetResult(),
            null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await signalled.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // the host is stopping for another reason
        }
        finally
        {
            registration.Unregister(null);
        }
        logger.LogInformation("Exit requested through the user-mode exit event; stopping");
        lifetime.StopApplication();
    }
}
