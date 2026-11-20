using System.Runtime.InteropServices;
using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Native;

/// <summary>
/// Reads another process's image path, command line and (allowlisted only) environment variables
/// straight from its PEB. x64 only. Never stores or returns any non-allowlisted environment variable.
/// </summary>
public sealed class PebProcessInspector : IProcessInspector
{
    private const int MaxCommandLineChars = 2048;
    private const int MaxEnvironmentBytes = 4 << 20;
    private const int StillActive = 259;

    // x64 offsets (verified): PEB.ProcessParameters, RTL_USER_PROCESS_PARAMETERS.{CommandLine,Environment,EnvironmentSize}
    private const int PebProcessParameters = 0x20, ParamsCommandLine = 0x70, ParamsEnvironment = 0x80, ParamsEnvironmentSize = 0x3F0;

    private const string SessionIdName = "CLAUDE_CODE_SESSION_ID";
    private const string HostSessionIdName = "CLAUDE_CODE_HOST_SESSION_ID";
    private const string EntrypointName = "CLAUDE_CODE_ENTRYPOINT";

    public InspectResult Inspect(ProcessKey key)
    {
        nint handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessVmRead, false, key.Pid);
        if (handle == 0) return Failure(Marshal.GetLastPInvokeError());
        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _))
                return Failure(Marshal.GetLastPInvokeError());
            if (created != key.CreateTimeTicks) return Status(InspectStatus.Gone); // PID reused

            string? image = QueryImagePath(handle);
            if (NativeMethods.IsWow64Process2(handle, out ushort machine, out _) && machine != 0)
                return new InspectResult(InspectStatus.Unsupported, null, null, null, image, null);

            return ReadPeb(handle, image);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    private static unsafe InspectResult ReadPeb(nint handle, string? image)
    {
        // PROCESS_BASIC_INFORMATION is 48 bytes on x64; PebBaseAddress is at offset 8.
        nint* pbi = stackalloc nint[6];
        int status = NativeMethods.NtQueryInformationProcess(handle, 0, pbi, 48, out _);
        if (status < 0) return ReadFailure(handle, image, NativeMethods.ErrorAccessDenied);
        nint peb = pbi[1];

        if (!TryReadPointer(handle, peb + PebProcessParameters, out nint parameters) || parameters == 0)
            return ReadFailure(handle, image, Marshal.GetLastPInvokeError());

        string? commandLine = ReadCommandLine(handle, parameters);

        if (!TryReadPointer(handle, parameters + ParamsEnvironment, out nint env)
            || !TryReadPointer(handle, parameters + ParamsEnvironmentSize, out nint envSize)
            || env == 0 || envSize <= 0)
            return ReadFailure(handle, image, Marshal.GetLastPInvokeError());

        int size = (int)Math.Min(envSize, MaxEnvironmentBytes) & ~1;
        byte[] block = new byte[size];
        nuint read;
        bool ok;
        fixed (byte* b = block) ok = NativeMethods.ReadProcessMemory(handle, env, b, (nuint)size, out read);
        // A partial read (ERROR_PARTIAL_COPY) still holds usable leading data.
        if (!ok && read == 0) return ReadFailure(handle, image, Marshal.GetLastPInvokeError());

        string? sessionId = null, hostSessionId = null, entrypoint = null;
        ParseEnvironment(MemoryMarshal.Cast<byte, char>(block.AsSpan(0, (int)read & ~1)),
            ref sessionId, ref hostSessionId, ref entrypoint);
        Array.Clear(block); // do not leave the token-bearing environment lying around
        return new InspectResult(InspectStatus.Ok, sessionId, hostSessionId, entrypoint, image, commandLine);
    }

    private static void ParseEnvironment(ReadOnlySpan<char> block, ref string? sessionId, ref string? hostSessionId, ref string? entrypoint)
    {
        while (!block.IsEmpty)
        {
            int end = block.IndexOf('\0');
            if (end == 0) break; // empty entry terminates the block
            var entry = end < 0 ? block : block[..end];
            block = end < 0 ? default : block[(end + 1)..];

            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var name = entry[..eq];
            if (name.Equals(SessionIdName, StringComparison.OrdinalIgnoreCase)) sessionId = new string(entry[(eq + 1)..]);
            else if (name.Equals(HostSessionIdName, StringComparison.OrdinalIgnoreCase)) hostSessionId = new string(entry[(eq + 1)..]);
            else if (name.Equals(EntrypointName, StringComparison.OrdinalIgnoreCase)) entrypoint = new string(entry[(eq + 1)..]);
        }
    }

    private static unsafe string? ReadCommandLine(nint handle, nint parameters)
    {
        NativeMethods.UnicodeString us;
        nuint read;
        if (!NativeMethods.ReadProcessMemory(handle, parameters + ParamsCommandLine, (byte*)&us, (nuint)sizeof(NativeMethods.UnicodeString), out read)
            || us.Length == 0 || us.Buffer == 0)
            return null;
        int chars = Math.Min(us.Length / 2, MaxCommandLineChars);
        char[] buffer = new char[chars];
        fixed (char* c = buffer)
            if (!NativeMethods.ReadProcessMemory(handle, us.Buffer, (byte*)c, (nuint)(chars * 2), out read) && read == 0)
                return null;
        return new string(buffer, 0, (int)read / 2);
    }

    private static unsafe bool TryReadPointer(nint handle, nint address, out nint value)
    {
        nint v = 0;
        bool ok = NativeMethods.ReadProcessMemory(handle, address, (byte*)&v, (nuint)sizeof(nint), out nuint read) && read == (nuint)sizeof(nint);
        value = v;
        return ok;
    }

    private static unsafe string? QueryImagePath(nint handle)
    {
        char* buffer = stackalloc char[1024];
        int size = 1024;
        return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    /// <summary>A failed memory read: the process may have exited meanwhile, otherwise classify by error.</summary>
    private static InspectResult ReadFailure(nint handle, string? image, int error)
    {
        if (NativeMethods.GetExitCodeProcess(handle, out int code) && code != StillActive) return Status(InspectStatus.Gone);
        return new InspectResult(error == NativeMethods.ErrorAccessDenied ? InspectStatus.AccessDenied : InspectStatus.Unsupported,
            null, null, null, image, null);
    }

    private static InspectResult Failure(int error) => error switch
    {
        NativeMethods.ErrorAccessDenied => Status(InspectStatus.AccessDenied),
        NativeMethods.ErrorInvalidParameter => Status(InspectStatus.Gone),
        _ => Status(InspectStatus.Unsupported),
    };

    private static InspectResult Status(InspectStatus s) => new(s, null, null, null, null, null);
}
