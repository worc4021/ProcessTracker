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
