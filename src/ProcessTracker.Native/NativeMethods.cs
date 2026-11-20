using System.Runtime.InteropServices;

namespace ProcessTracker.Native;

internal static partial class NativeMethods
{
    public const int SystemProcessInformation = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQuerySystemInformation(int systemInformationClass, byte* buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(out PerformanceInformation info, int size);

    public const uint ProcessQueryLimitedInformation = 0x1000, ProcessVmRead = 0x0010;
    public const int ErrorAccessDenied = 5, ErrorInvalidParameter = 87;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageNameW(nint process, int flags, char* buffer, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadProcessMemory(nint process, nint baseAddress, byte* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out int exitCode);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, void* info, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInformation
    {
        public int Cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public nint Buffer;
    }

    /// <summary>x64 SYSTEM_PROCESS_INFORMATION (256 bytes); SYSTEM_THREAD_INFORMATION entries follow it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInfo
    {
        public uint NextEntryOffset, NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount, NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime, UserTime, KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId, InheritedFromUniqueProcessId;
        public uint HandleCount, SessionId;
        public nint UniqueProcessKey;
        public nuint PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivatePageCount;
        public long ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    /// <summary>x64 SYSTEM_THREAD_INFORMATION (80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemThreadInfo
    {
        public long KernelTime, UserTime, CreateTime;
        public uint WaitTime;
        public nint StartAddress, ClientIdProcess, ClientIdThread;
        public int Priority, BasePriority;
        public uint ContextSwitches, ThreadState, WaitReason;
    }

    public const uint AfInet = 2, TcpTableOwnerPidConnections = 4, ErrorInsufficientBuffer = 122, MibTcpStateEstablished = 5;

    /// <summary>Writes a MIB_TCPTABLE_OWNER_PID (a DWORD row count, then the rows) for the given table class.</summary>
    [LibraryImport("iphlpapi.dll")]
    public static unsafe partial uint GetExtendedTcpTable(
        byte* tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint af, uint tableClass, uint reserved);

    /// <summary>MIB_TCPROW_OWNER_PID (24 bytes). Addresses and ports (low 16 bits) are in network byte order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    public const uint TokenQuery = 0x0008;
    public const int TokenUserClass = 1;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetTokenInformation(nint token, int infoClass, byte* info, int length, out int returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);
}
