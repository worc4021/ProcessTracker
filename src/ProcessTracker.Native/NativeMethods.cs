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
}
