namespace ProcessTracker.Service;

/// <summary>
/// Drives the tracker: one <see cref="TrackerHost.Restore"/>, then a tick every 2 s. On stop it waits (bounded) for
/// the in-flight tick, then disposes the host, which flushes the segment writer and closes the state store.
/// </summary>
public sealed class CollectorWorker(TrackerHost host, TimeProvider time, ILogger<CollectorWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowTick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InFlightTickWait = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never run the first (synchronous) tick on the host's startup path

        try { host.Restore(); }
        catch (Exception e) when (IsNonFatal(e))
        {
            log.LogError(e, "Restoring persisted tracker state failed; starting without it");
        }

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do RunTick();
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void RunTick()
    {
        var started = time.GetTimestamp();
        try { host.Tick(); }
        catch (Exception e) when (IsNonFatal(e))
        {
            log.LogError(e, "Collector tick failed");
        }
        var elapsed = time.GetElapsedTime(started);
        if (elapsed > SlowTick) log.LogWarning("Collector tick took {Elapsed} ms", (long)elapsed.TotalMilliseconds);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            // base.StopAsync gives up when the host's shutdown token fires; give the in-flight tick a bounded wait.
            if (ExecuteTask is { IsCompleted: false } loop)
            {
                try { await loop.WaitAsync(InFlightTickWait, CancellationToken.None); }
                catch (TimeoutException)
                {
                    log.LogError("Collector tick still running after {Seconds} s", InFlightTickWait.TotalSeconds);
                }
                catch (Exception e) when (IsNonFatal(e))
                {
                    log.LogError(e, "Collector loop failed");
                }
            }

            // A faulted loop still gets flushed. Dispose itself refuses to run while a tick is in flight.
            host.Dispose();
            log.LogInformation("Collector stopped");
        }
    }

    private static bool IsNonFatal(Exception e) => e is not (OutOfMemoryException or AccessViolationException);
}
