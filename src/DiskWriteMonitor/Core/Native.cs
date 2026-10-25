using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 所有 Win32 调用集中在这里。产品只读，不写任何设备。
/// </summary>
internal static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;

    public const uint GENERIC_WRITE = 0x40000000;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // STORAGE_BUS_TYPE
    public const uint BusTypeNvme = 17;

    // ERROR_ACCESS_DENIED
    public const int ErrorAccessDenied = 5;
    public const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessIoCounters(SafeProcessHandle hProcess, out IoCounters lpIoCounters);

    [DllImport("shell32.dll")]
    public static extern bool IsUserAnAdmin();

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    public static bool IsElevated => IsUserAnAdmin();

    public static string DescribeBus(uint bus) => bus switch
    {
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        14 => "Virtual",
        17 => "NVMe",
        _ => "Bus" + bus
    };

    public static void WriteI32(byte[] buf, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buf, offset);

    public static int ReadI32(byte[] buf, int offset) => BitConverter.ToInt32(buf, offset);

    public static string ReadAnsiZ(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return string.Empty;
        var end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }
}
