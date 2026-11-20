using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Native;

/// <summary>
/// Finds the profile directory of a process's owner: process token user SID, then the ProfileList registry key.
/// Profile paths are cached per SID.
/// </summary>
public sealed class ProfileResolver : IProfileResolver
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private readonly ConcurrentDictionary<string, string> _bySid = new(StringComparer.OrdinalIgnoreCase);

    public string? ProfileOf(ProcessKey key)
    {
        var sid = OwnerSid(key);
        if (sid is null) return null;
        if (_bySid.TryGetValue(sid, out var cached)) return cached;

        var path = ReadProfilePath(sid);
        if (path is not null) _bySid[sid] = path;
        return path;
    }

    private static string? ReadProfilePath(string sid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ProfileListKey + @"\" + sid);
            return k?.GetValue("ProfileImagePath") is string raw && raw.Length > 0
                ? Environment.ExpandEnvironmentVariables(raw)
                : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static unsafe string? OwnerSid(ProcessKey key)
    {
        nint process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, key.Pid);
        if (process == 0) return null;
        nint token = 0;
        try
        {
            if (!NativeMethods.GetProcessTimes(process, out long created, out _, out _, out _)
                || created != key.CreateTimeTicks) return null; // gone, or the PID was reused
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out token)) return null;

            // TOKEN_USER: a SID_AND_ATTRIBUTES whose first field is a pointer into the same buffer.
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, null, 0, out int needed);
            if (needed <= 0) return null;
            var buffer = new byte[needed];
            fixed (byte* b = buffer)
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, b, needed, out _)) return null;
                if (!NativeMethods.ConvertSidToStringSid(*(nint*)b, out nint str)) return null;
                try { return Marshal.PtrToStringUni(str); }
                finally { NativeMethods.LocalFree(str); }
            }
        }
        finally
        {
            if (token != 0) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }
}
