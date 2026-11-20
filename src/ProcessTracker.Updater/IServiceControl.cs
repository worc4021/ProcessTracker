namespace ProcessTracker.Updater;

/// <summary>Controls the ProcessTracker Windows service.</summary>
public interface IServiceControl
{
    /// <summary>Stops the service and waits until it has stopped; throws if it has not stopped within <paramref name="timeout"/>.</summary>
    void Stop(TimeSpan timeout);

    /// <summary>Starts the service (a no-op when it is already running); readiness is judged by the health probe.</summary>
    void Start();
}
