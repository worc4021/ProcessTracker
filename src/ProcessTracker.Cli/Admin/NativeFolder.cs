using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ProcessTracker.Cli.Admin;

/// <summary>One ACE as the trust decision sees it.</summary>
public sealed record AceInfo(SecurityIdentifier Sid, int Mask, bool IsAllow);

/// <summary>
/// Handle-based folder operations: everything is decided and applied through one handle, so a folder swapped for a
/// link or another folder between check and use cannot redirect the change.
/// </summary>
internal static class NativeFolder
{
    private const uint Delete = 0x10000, ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000, FileReadAttributes = 0x80, Synchronize = 0x100000;
    // No FILE_SHARE_DELETE. Share modes are only checked for handles with data or DELETE access, so the open below
    // requests DELETE: that is what stops anyone (including us) renaming or replacing the folder while it is held, and
    // makes the open fail if a user already holds a handle with DELETE.
    private const uint ShareRead = 1, ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const uint OwnerInfo = 1, DaclInfo = 4, ProtectedDacl = 0x80000000;
    private const uint ReparseAttribute = 0x400, DirectoryAttribute = 0x10;
    public const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAlreadyExists = 183, ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh; // FILETIMEs as uint pairs: native layout is 52 bytes
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

    /// <summary>Creates the directory with <paramref name="descriptor"/> (self-relative) from birth; false when something is already there.</summary>
    public static bool TryCreate(string path, byte[] descriptor)
    {
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            if (CreateDirectoryW(path, ref sa)) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorAlreadyExists) return false;
            throw new Win32Exception(error);
        }
        finally { pinned.Free(); }
    }

    /// <summary>Opens the path itself (a link is opened as a link). Null with the Win32 error when it cannot be opened.</summary>
    public static SafeFileHandle? Open(string path, out int error)
    {
        var handle = CreateFileW(path, Delete | ReadControl | WriteDac | WriteOwner | FileReadAttributes | Synchronize, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }
        error = 0;
        return handle;
    }

    /// <summary>True for a real directory: not a link, not a file.</summary>
    public static bool IsPlainDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (info.Attributes & ReparseAttribute) == 0 && (info.Attributes & DirectoryAttribute) != 0;
    }

    /// <summary>Owner and explicit ACEs read through the handle. A null DACL (everyone full control) is one allow-all ACE for Everyone.</summary>
    public static (SecurityIdentifier? Owner, IReadOnlyList<AceInfo> Aces) ReadSecurity(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, OwnerInfo | DaclInfo, null, 0, out var needed);
        var buffer = new byte[needed];
        if (!GetKernelObjectSecurity(handle, OwnerInfo | DaclInfo, buffer, needed, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var sd = new RawSecurityDescriptor(buffer, 0);
        var aces = new List<AceInfo>();
        if (sd.DiscretionaryAcl is null)
            aces.Add(new AceInfo(new SecurityIdentifier(WellKnownSidType.WorldSid, null), -1, true));
        else
            foreach (var ace in sd.DiscretionaryAcl)
                if (ace is KnownAce known)
                    aces.Add(new AceInfo(known.SecurityIdentifier, known.AccessMask,
                        ace.AceType is AceType.AccessAllowed or AceType.AccessAllowedCallback or AceType.AccessAllowedObject or AceType.AccessAllowedCallbackObject));
                else
                    aces.Add(new AceInfo(new SecurityIdentifier(WellKnownSidType.WorldSid, null), -1, true)); // unknown ACE type: assume the worst
        return (sd.Owner, aces);
    }

    /// <summary>Sets owner and a protected DACL through the handle.</summary>
    public static void WriteSecurity(SafeFileHandle handle, byte[] descriptor)
    {
        if (!SetKernelObjectSecurity(handle, OwnerInfo | DaclInfo | ProtectedDacl, descriptor)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>Renames the path itself (a link is renamed, never its target).</summary>
    public static void Rename(string from, string to)
    {
        if (!MoveFileExW(from, to, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}
