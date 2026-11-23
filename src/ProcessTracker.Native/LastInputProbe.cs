using System.Runtime.InteropServices;
using ProcessTracker.Core.Hooks;

namespace ProcessTracker.Native;

/// <summary>
/// The interactive user's last keyboard or mouse input, from <c>GetLastInputInfo</c>. Meaningful only in the user's own
/// desktop session (user and dev modes); the service in session 0 uses a probe that always says "unknown".
/// </summary>
public sealed class LastInputProbe : IUserActivity
{
    public TimeSpan? SinceLastInput()
    {
        var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        return NativeMethods.GetLastInputInfo(ref info) ? Elapsed(info.Time, Environment.TickCount64) : null;
    }

    /// <summary>
    /// Time from a 32-bit input tick to now. The 32-bit tick wraps every 49.7 days, so the difference is taken modulo
    /// 2^32 against the low 32 bits of <paramref name="nowTick64"/>.
    /// </summary>
    public static TimeSpan Elapsed(uint lastInputTick, long nowTick64) =>
        TimeSpan.FromMilliseconds(unchecked((uint)nowTick64 - lastInputTick));
}
