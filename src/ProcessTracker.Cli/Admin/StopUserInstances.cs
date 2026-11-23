using System.Diagnostics;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Admin;

/// <summary>
/// Asks every running user-mode instance to exit (the MSI runs this as SYSTEM before it starts the machine service),
/// then waits until the single-instance mutex is free. The names are parameters for tests; production uses the
/// defaults.
/// </summary>
public sealed class StopUserInstances(
    string eventName = UserModeSignal.ExitEventName,
    string mutexName = UserModeSignal.InstanceMutexName,
    TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public SetupResult Run()
    {
        bool signalled;
        try { signalled = UserModeSignal.TrySignal(eventName); }
        catch (UnauthorizedAccessException)
        {
            return new SetupResult(false, "A user-mode ProcessTracker is running under another account; run this elevated.");
        }
        if (!signalled) return new SetupResult(true, "No user-mode ProcessTracker is running.");

        return WaitForMutexFree()
            ? new SetupResult(true, "User-mode ProcessTracker stopped.")
            : new SetupResult(false, $"User-mode ProcessTracker did not stop within {_timeout.TotalSeconds:0.#} s.");
    }

    /// <summary>True once the mutex could be acquired (and was released again) within the timeout.</summary>
    private bool WaitForMutexFree()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = _timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            Mutex mutex;
            try { mutex = new Mutex(initiallyOwned: false, mutexName); }
            catch (UnauthorizedAccessException) // held under a DACL that excludes us: wait for its owner to go away
            {
                Thread.Sleep(remaining < RetryDelay ? remaining : RetryDelay);
                continue;
            }
            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(remaining); }
                catch (AbandonedMutexException) { acquired = true; } // the instance died holding it: free now
                if (!acquired) return false;
                mutex.ReleaseMutex();
                return true;
            }
        }
    }
}
