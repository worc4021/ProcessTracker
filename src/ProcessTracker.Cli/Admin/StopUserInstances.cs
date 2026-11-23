using System.Diagnostics;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// Asks every running user-mode instance to exit (the MSI runs this as SYSTEM before it starts the machine service)
/// and waits until the single-instance mutex is free. While the mutex is held the exit event is signalled again on
/// every retry: an instance may hold the mutex before it has created its event, or still hold it after closing it.
/// Succeeds only once the mutex could be acquired (and was released again) or does not exist. The names are
/// parameters for tests; production uses the defaults.
/// </summary>
public sealed class StopUserInstances(
    string eventName = UserModeSignal.ExitEventName,
    string mutexName = UserModeSignal.InstanceMutexName,
    TimeSpan? timeout = null)
{
    /// <summary>Beyond user mode's own 15 s host shutdown plus its final flush, so a busy instance is not given up on early.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public SetupResult Run()
    {
        var clock = Stopwatch.StartNew();
        var signalled = false;
        while (true)
        {
            try { signalled |= UserModeSignal.TrySignal(eventName); }
            catch (UnauthorizedAccessException)
            {
                return new SetupResult(false, "A user-mode ProcessTracker is running under another account; run this elevated.");
            }

            var remaining = _timeout - clock.Elapsed;
            if (MutexFree(remaining < RetryDelay ? remaining : RetryDelay))
                return signalled
                    ? new SetupResult(true, "User-mode ProcessTracker stopped.")
                    : new SetupResult(true, "No user-mode ProcessTracker is running.");
            if (clock.Elapsed >= _timeout)
                return new SetupResult(false, signalled
                    ? $"User-mode ProcessTracker did not stop within {_timeout.TotalSeconds:0.#} s."
                    : $"{mutexName} is still held after {_timeout.TotalSeconds:0.#} s and no user-mode exit event appeared.");
        }
    }

    /// <summary>True when the mutex does not exist, or could be acquired (and was released again) within <paramref name="wait"/>.</summary>
    private bool MutexFree(TimeSpan wait)
    {
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        Mutex? mutex;
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out mutex)) return true;
        }
        catch (UnauthorizedAccessException) // held under a DACL that excludes us: wait for its owner to go away
        {
            Thread.Sleep(wait);
            return false;
        }
        using (mutex)
        {
            bool acquired;
            try { acquired = mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { acquired = true; } // the instance died holding it: ours now, so free
            if (!acquired) return false;
            mutex.ReleaseMutex();
            return true;
        }
    }
}
