using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessTracker.Service.Tests.Updates;

/// <summary>Creates an NTFS junction (needs no privilege, unlike a directory symbolic link). Test helper only.</summary>
public static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    public static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));

        using var data = new MemoryStream();
        using (var w = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true))
        {
            w.Write(IoReparseTagMountPoint);
            w.Write((ushort)(8 + substitute.Length + 2 + print.Length + 2)); // ReparseDataLength
            w.Write((ushort)0);
            w.Write((ushort)0);                        // SubstituteNameOffset
            w.Write((ushort)substitute.Length);        // SubstituteNameLength
            w.Write((ushort)(substitute.Length + 2));  // PrintNameOffset
            w.Write((ushort)print.Length);             // PrintNameLength
            w.Write(substitute); w.Write((ushort)0);
            w.Write(print); w.Write((ushort)0);
        }
        var buffer = data.ToArray();

        using var handle = CreateFile(link, GenericWrite, 0, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, nint outBuffer,
        int outSize, out int returned, nint overlapped);
}
