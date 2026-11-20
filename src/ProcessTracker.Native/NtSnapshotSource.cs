using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProcessTracker.Core.Collection;
using ProcessTracker.Core.Model;

namespace ProcessTracker.Native;

/// <summary>One-syscall system snapshot via NtQuerySystemInformation(SystemProcessInformation). x64 only.</summary>
public sealed class NtSnapshotSource : ISnapshotSource
{
    // Pinned-object-heap buffer: address is stable, so UNICODE_STRING.Buffer pointers can be turned into offsets.
    private byte[] _buffer = GC.AllocateUninitializedArray<byte>(1 << 20, pinned: true);

    public unsafe Snapshot Take()
    {
        int length = Query();
        var data = _buffer.AsSpan(0, length);
        nint baseAddress = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffer));
        int procSize = Unsafe.SizeOf<NativeMethods.SystemProcessInfo>();
        int threadSize = Unsafe.SizeOf<NativeMethods.SystemThreadInfo>();

        var processes = new List<RawProcessCounters>(512);
        int offset = 0;
        while (true)
        {
            var p = MemoryMarshal.Read<NativeMethods.SystemProcessInfo>(data[offset..]);
            int pid = (int)p.UniqueProcessId;
            if (pid != 0)
            {
                long switches = 0;
                int threadOffset = offset + procSize;
                for (uint i = 0; i < p.NumberOfThreads && threadOffset + threadSize <= data.Length; i++, threadOffset += threadSize)
                    switches += MemoryMarshal.Read<NativeMethods.SystemThreadInfo>(data[threadOffset..]).ContextSwitches;

                processes.Add(new RawProcessCounters(
                    new ProcessKey(pid, p.CreateTime),
                    (int)p.InheritedFromUniqueProcessId,
                    ReadName(data, baseAddress, p.ImageName),
                    p.KernelTime, p.UserTime,
                    (long)p.PrivatePageCount, (long)p.WorkingSetSize,
                    p.ReadOperationCount, p.WriteOperationCount, p.OtherOperationCount,
                    p.ReadTransferCount, p.WriteTransferCount, p.OtherTransferCount,
                    switches, (int)p.HandleCount, (int)p.NumberOfThreads));
            }
            if (p.NextEntryOffset == 0) break;
            offset += (int)p.NextEntryOffset;
        }

        return new Snapshot(DateTime.UtcNow, processes, ReadSystemCounters());
    }

    private static string ReadName(ReadOnlySpan<byte> data, nint baseAddress, NativeMethods.UnicodeString name)
    {
        if (name.Length == 0 || name.Buffer == 0) return string.Empty;
        long start = name.Buffer - baseAddress;
        if (start < 0 || start + name.Length > data.Length) return string.Empty;
        return new string(MemoryMarshal.Cast<byte, char>(data.Slice((int)start, name.Length)));
    }

    private unsafe int Query()
    {
        while (true)
        {
            int status;
            int needed;
            fixed (byte* p = _buffer)
                status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, p, _buffer.Length, out needed);
            if (status >= 0) return needed > 0 && needed <= _buffer.Length ? needed : _buffer.Length;
            if (status != NativeMethods.StatusInfoLengthMismatch)
                throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            // Processes may appear between calls: over-allocate, and keep the larger buffer.
            _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(needed, _buffer.Length) + (256 << 10), pinned: true);
        }
    }

    private static SystemCounters ReadSystemCounters()
    {
        if (!NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user))
            throw new InvalidOperationException($"GetSystemTimes failed: {Marshal.GetLastPInvokeError()}");
        if (!NativeMethods.GetPerformanceInfo(out var info, Unsafe.SizeOf<NativeMethods.PerformanceInformation>()))
            throw new InvalidOperationException($"GetPerformanceInfo failed: {Marshal.GetLastPInvokeError()}");
        long page = (long)info.PageSize;
        return new SystemCounters(Environment.ProcessorCount, idle, kernel, user,
            (long)info.CommitTotal * page, (long)info.CommitLimit * page,
            (long)info.PhysicalAvailable * page, (long)info.PhysicalTotal * page);
    }
}
