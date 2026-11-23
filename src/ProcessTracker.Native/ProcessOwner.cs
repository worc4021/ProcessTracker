using System.Security.Principal;

namespace ProcessTracker.Native;

/// <summary>Who a process runs as.</summary>
public static class ProcessOwner
{
    /// <summary>
    /// The user SID of the process's primary token, or null if the process does not exist or cannot be queried
    /// (for instance a process of another account that denies even limited-information access).
    /// </summary>
    public static unsafe SecurityIdentifier? SidOf(int pid)
    {
        nint process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return null;
        nint token = 0;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out token)) return null;

            // TOKEN_USER: a SID_AND_ATTRIBUTES whose first field is a pointer into the same buffer.
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, null, 0, out int needed);
            if (needed <= 0) return null;
            var buffer = new byte[needed];
            fixed (byte* b = buffer)
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, b, needed, out _)) return null;
                return new SecurityIdentifier(*(nint*)b);
            }
        }
        finally
        {
            if (token != 0) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }
}
