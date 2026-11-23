using System.Runtime.InteropServices;

namespace ProcessTracker.Native;

/// <summary>Asks the Service Control Manager whether a Windows service is installed. Needs no admin rights.</summary>
public static class ServiceProbe
{
    /// <summary>The machine-wide service the MSI installs; user mode yields to it.</summary>
    public const string MachineServiceName = "ProcessTracker";

    /// <summary>True when the machine-wide <c>ProcessTracker</c> service is installed (running or not).</summary>
    public static bool IsMachineServiceInstalled() => IsInstalled(MachineServiceName);

    /// <summary>
    /// True when a service of that name is installed. A service whose handle is denied to us still exists, so it counts
    /// as installed. False when the SCM itself cannot be opened or answers anything else: nothing we can see to yield to.
    /// </summary>
    public static bool IsInstalled(string serviceName)
    {
        var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (scm == 0) return false;
        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, NativeMethods.ServiceQueryStatus);
            if (service != 0)
            {
                NativeMethods.CloseServiceHandle(service);
                return true;
            }
            return Marshal.GetLastPInvokeError() == NativeMethods.ErrorAccessDenied;
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }
}
