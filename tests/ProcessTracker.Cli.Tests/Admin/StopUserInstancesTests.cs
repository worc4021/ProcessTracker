using ProcessTracker.Cli.Admin;
using ProcessTracker.Native;

namespace ProcessTracker.Cli.Tests.Admin;

/// <summary>
/// Test-specific event and mutex names only: never <c>Global\ProcessTracker.Service</c> or the real exit event
/// (ruling R1).
/// </summary>
public class StopUserInstancesTests
{
    private static (string Event, string Mutex) TestNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($@"Global\ProcessTracker.Test.Exit.{id}", $@"Global\ProcessTracker.Test.Mutex.{id}");
    }

    [Fact]
    public void StopUserInstances_NoEvent_ExitsZero()
    {
        var (evt, mutex) = TestNames();

        var result = new StopUserInstances(evt, mutex, TimeSpan.FromMilliseconds(200)).Run();

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void StopUserInstances_MutexHeld_TimesOut()
    {
        var (evt, mutex) = TestNames();
        using var instanceEvent = UserModeSignal.CreateForInstance(evt);
        // A mutex is re-entrant for its owning thread, so the instance's mutex is held on a thread of its own.
        using var holder = new MutexHolder(mutex);

        var result = new StopUserInstances(evt, mutex, TimeSpan.FromMilliseconds(300)).Run();

        Assert.False(result.Ok); // AdminCommand reports a failed result with exit code 1
        Assert.True(instanceEvent.WaitOne(0)); // the instance was asked to exit
        Assert.Equal("User-mode ProcessTracker did not stop within 0.3 s.", result.Message);
    }

    [Fact]
    public void StopUserInstances_InstanceReleasesMutex_ExitsZero()
    {
        var (evt, mutex) = TestNames();
        using var instanceEvent = UserModeSignal.CreateForInstance(evt);
        using var holder = new MutexHolder(mutex, releaseWhenSet: instanceEvent); // exits when signalled, as an instance does

        var result = new StopUserInstances(evt, mutex, TimeSpan.FromSeconds(10)).Run();

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void StopUserInstances_MutexHeldEventAppearsLater_SignalsAndExitsZero()
    {
        // An instance between acquiring the mutex and creating its exit event: keep signalling until it exits.
        var (evt, mutex) = TestNames();
        using var holder = new LateEventInstance(mutex, evt, TimeSpan.FromMilliseconds(400));

        var result = new StopUserInstances(evt, mutex, TimeSpan.FromSeconds(10)).Run();

        Assert.True(result.Ok, result.Message);
        Assert.True(holder.Signalled); // it exited because it was asked to, not because the test ended
    }

    [Fact]
    public void StopUserInstances_MutexHeldNoEvent_TimesOut()
    {
        var (evt, mutex) = TestNames();
        using var holder = new MutexHolder(mutex);

        var result = new StopUserInstances(evt, mutex, TimeSpan.FromMilliseconds(300)).Run();

        Assert.False(result.Ok, result.Message);
        Assert.Equal($"{mutex} is still held after 0.3 s and no user-mode exit event appeared.", result.Message);
    }

    /// <summary>
    /// A user-mode instance that holds the mutex, creates its exit event only after <c>delay</c>, then waits for the
    /// signal and releases the mutex.
    /// </summary>
    private sealed class LateEventInstance : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private volatile bool _signalled;

        public bool Signalled => _signalled;

        public LateEventInstance(string mutexName, string eventName, TimeSpan delay)
        {
            using var acquired = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var m = new Mutex(initiallyOwned: true, mutexName);
                acquired.Set();
                if (!_release.Wait(delay))
                {
                    using var exit = UserModeSignal.CreateForInstance(eventName);
                    _signalled = WaitHandle.WaitAny([exit, _release.WaitHandle]) == 0;
                }
                m.ReleaseMutex();
            }) { IsBackground = true };
            _thread.Start();
            acquired.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }
    }

    /// <summary>Holds a named mutex on a dedicated thread until disposed, or until <c>releaseWhenSet</c> is set.</summary>
    private sealed class MutexHolder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public MutexHolder(string name, WaitHandle? releaseWhenSet = null)
        {
            using var acquired = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var m = new Mutex(initiallyOwned: true, name);
                acquired.Set();
                if (releaseWhenSet is null) _release.Wait();
                else WaitHandle.WaitAny([releaseWhenSet, _release.WaitHandle]);
                m.ReleaseMutex();
            }) { IsBackground = true };
            _thread.Start();
            acquired.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }
    }
}
