using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcessTracker.Core.Sessions;

/// <summary>
/// Checks on an already opened file handle, closing the gap between checking a path and opening it: the handle's
/// final path must be exactly the checked path, and the file must have a single name (no hard link).
/// </summary>
internal static class FileHandleChecks
{
    private const uint FileNameNormalized = 0x0, VolumeNameDos = 0x0;
    private const string LongPrefix = @"\\?\";

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are 4-byte aligned in BY_HANDLE_FILE_INFORMATION
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps Core free of unsafe code; both signatures are blittable.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
#pragma warning restore SYSLIB1054

    /// <summary>The handle's normalized DOS path without the <c>\\?\</c> prefix; null on failure.</summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        var buf = new char[512];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var n = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, FileNameNormalized | VolumeNameDos);
            if (n == 0) return null;
            if (n < buf.Length)
            {
                var path = new string(buf, 0, (int)n);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null; // never a network file
                return path.StartsWith(LongPrefix, StringComparison.Ordinal) ? path[LongPrefix.Length..] : path;
            }
            if (n > 32 * 1024) return null;
            buf = new char[n + 1];
        }
        return null;
    }

    /// <summary>
    /// The final path of a directory (long names, junctions resolved); null when it cannot be opened. Used for the
    /// trusted profile root only, so that expected paths compare equal to final paths.
    /// </summary>
    public static string? DirectoryFinalPath(string directory)
    {
        const uint FileReadAttributes = 0x80, FileFlagBackupSemantics = 0x02000000;
        using var h = CreateFileW(directory, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open,
            FileFlagBackupSemantics, 0);
        return h.IsInvalid ? null : FinalPath(h);
    }

    /// <summary>
    /// True when the handle's final path equals <paramref name="expectedPath"/> (case-insensitively) and the file has
    /// exactly one link. False on any failure.
    /// </summary>
    public static bool IsExactlyAt(SafeFileHandle handle, string expectedPath)
    {
        if (FinalPath(handle) is not { } final) return false;
        if (!string.Equals(final, expectedPath, StringComparison.OrdinalIgnoreCase)) return false;
        return GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks == 1;
    }
}
