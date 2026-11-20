using System.ServiceProcess;

namespace ProcessTracker.Updater;

/// <summary>Controls a Windows service through the Service Control Manager (needs elevation).</summary>
public sealed class WindowsServiceControl(string serviceName = "ProcessTracker") : IServiceControl
{
    /// <summary>How long <see cref="Start"/> waits for a service that is still stopping.</summary>
    private static readonly TimeSpan StopPendingWait = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    /// <exception cref="System.ServiceProcess.TimeoutException">The service did not stop in time.</exception>
    public void Stop(TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    /// <inheritdoc />
    public void Start()
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.StopPending) sc.WaitForStatus(ServiceControllerStatus.Stopped, StopPendingWait);
        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
    }
}
