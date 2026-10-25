using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskProbe;

internal static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // 设备/磁盘属性
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;
    public const uint IOCTL_SMART_RCV_DRIVE_DATA = 0x0007C088;
    public const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint GENERIC_WRITE = 0x40000000;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

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
}

internal readonly record struct NvmeHealth(bool Ok, int Win32, byte Critical, ushort TempK, byte Used, ulong Units, string Note);

internal sealed class Report
{
    private readonly StringBuilder _sb = new();

    public void Line(string s = "") => _sb.AppendLine(s);
    public void Section(string title) { _sb.AppendLine(); _sb.AppendLine("=== " + title + " ==="); }

    public string Text => _sb.ToString();

    public void Save(string path)
    {
        try { File.WriteAllText(path, _sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { Console.WriteLine("WARN: cannot write report: " + ex.Message); }
    }
}

internal static class Program
{
    private const double UnitBytes = 512_000.0; // NVMe: 1 Data Unit = 1000 x 512 bytes
    private const string PhysicalDrivePrefix = @"\\.\PhysicalDrive";
    private const string VolPrefix = @"\\.\";

    private static int Main()
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 某些宿主不支持，忽略 */ }

        var r = new Report();
        var swAll = Stopwatch.StartNew();

        r.Line("DiskWriteMonitor - data availability probe");
        r.Line("time      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        r.Line("elevated  : " + Native.IsUserAnAdmin());
        r.Line("os        : " + Environment.OSVersion.VersionString);
        r.Line("arch      : " + RuntimeInformation.ProcessArchitecture);
        r.Line("exe       : " + Environment.ProcessPath);

        ProbeDrives(r);
        ProbeVolumes(r);
        ProbeSata(r);
        ProbeProcesses(r);

        r.Line();
        r.Line("total probe time: " + swAll.ElapsedMilliseconds + " ms");

        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, Native.IsUserAnAdmin() ? "report-admin.txt" : "report-user.txt");
        r.Save(path);

        Console.WriteLine();
        Console.WriteLine("--- probe finished ---");
        Console.WriteLine("elevated: " + Native.IsUserAnAdmin());
        Console.WriteLine("report  : " + path);

        // 只有带 --pause 才等回车，避免在自动化运行时卡住
        if (Environment.GetCommandLineArgs().Contains("--pause"))
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            try { Console.ReadLine(); } catch { /* 无控制台时忽略 */ }
        }

        return 0;
    }

    // ---------------- 磁盘 ----------------

    private static void ProbeDrives(Report r)
    {
        r.Section("physical drives");

        for (var i = 0; i < 16; i++)
        {
            var path = PhysicalDrivePrefix + i;

            // 1) 带读权限打开（这正是读 SMART 需要的权限）
            using var read = Native.CreateFileW(path, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            var readErr = Marshal.GetLastWin32Error();

            // 错误 2 = 该编号磁盘不存在，后面的更不会有
            if (read.IsInvalid && readErr == 2) break;

            r.Line();
            r.Line($"[PhysicalDrive{i}] {path}");
            r.Line($"  open GENERIC_READ   : {(read.IsInvalid ? "FAIL win32=" + readErr : "OK")}");

            // 2) 零权限打开（部分 IOCTL 不需要读权限，用来区分"没权限"和"设备不支持"）
            using var none = Native.CreateFileW(path, 0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            var noneErr = Marshal.GetLastWin32Error();
            r.Line($"  open access=0       : {(none.IsInvalid ? "FAIL win32=" + noneErr : "OK")}");

            var usable = read.IsInvalid ? none : read;
            var hasHandle = !usable.IsInvalid;

            if (!hasHandle)
            {
                r.Line("  => skipped (no handle, likely needs elevation)");
                continue;
            }

            var model = ReadModel(usable);
            r.Line("  model               : " + (model ?? "(unknown)"));

            var bus = ReadBusType(usable);
            r.Line("  bus type            : " + DescribeBus(bus));

            var devNum = ReadDeviceNumber(usable);
            r.Line("  device number       : " + (devNum >= 0 ? devNum.ToString() : "(n/a)"));

            if (bus == 17)
            {
                var t = Stopwatch.StartNew();
                var h = QueryNvmeHealth(usable);
                t.Stop();
                if (h.Ok)
                {
                    var total = h.Units * UnitBytes;
                    var tb = total / 1e12;
                    var tempC = h.TempK >= 273 ? h.TempK - 273 : h.TempK;
                    r.Line($"  NVMe health (IOCTL) : OK in {t.ElapsedMilliseconds} ms");
                    r.Line($"    data units written: {h.Units:N0}");
                    r.Line($"    firmware total    : {FormatBytes(total)}  ({tb:F3} TB)");
                    r.Line($"    percentage used   : {h.Used} %");
                    r.Line($"    temperature       : {h.TempK} K = {tempC} C   (critical warning {h.Critical})");
                }
                else
                {
                    r.Line($"  NVMe health (IOCTL) : FAIL win32={h.Win32} in {t.ElapsedMilliseconds} ms - {h.Note}");
                }
            }
            else
            {
                r.Line("  NVMe health (IOCTL) : not attempted (bus is not NVMe)");
            }
        }
    }

    private static string? ReadModel(SafeFileHandle h)
    {
        var input = new byte[8];
        WriteI32(input, 0, 0); // StorageDeviceProperty
        WriteI32(input, 4, 0); // PropertyStandardQuery
        var output = new byte[1024];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero) || returned < 36)
            return null;

        var productOffset = ReadI32(output, 16);
        if (productOffset <= 0 || productOffset >= returned) return null;
        var raw = ReadAnsiZ(output, productOffset);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static uint ReadBusType(SafeFileHandle h)
    {
        var input = new byte[8];
        WriteI32(input, 0, 0);
        WriteI32(input, 4, 0);
        var output = new byte[1024];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero) || returned < 32)
            return 0;
        return BitConverter.ToUInt32(output, 28);
    }

    private static int ReadDeviceNumber(SafeFileHandle h)
    {
        var output = new byte[16];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER, Array.Empty<byte>(), 0,
                output, output.Length, out _, IntPtr.Zero))
            return -1;
        return BitConverter.ToInt32(output, 4); // STORAGE_DEVICE_NUMBER.DeviceNumber
    }

    private static NvmeHealth QueryNvmeHealth(SafeFileHandle h)
    {
        const int headerSize = 8;   // STORAGE_PROPERTY_QUERY
        const int protoSize = 40;   // STORAGE_PROTOCOL_SPECIFIC_DATA
        const int logSize = 512;    // NVME_HEALTH_INFO_LOG

        var input = new byte[headerSize + protoSize];
        WriteI32(input, 0, 49); // StorageDeviceProtocolSpecificProperty
        WriteI32(input, 4, 0);  // PropertyStandardQuery
        const int p = headerSize;
        WriteI32(input, p + 0, 3);          // ProtocolTypeNvme
        WriteI32(input, p + 4, 2);          // NVMeDataTypeLogPage
        WriteI32(input, p + 8, 0x02);       // NVME_LOG_PAGE_HEALTH_INFO
        WriteI32(input, p + 12, 0);
        WriteI32(input, p + 16, protoSize); // ProtocolDataOffset
        WriteI32(input, p + 20, logSize);   // ProtocolDataLength

        var output = new byte[headerSize + protoSize + logSize + 64];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero))
        {
            return new NvmeHealth(false, Marshal.GetLastWin32Error(), 0, 0, 0, 0,
                "DeviceIoControl failed, returned=" + returned);
        }

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR{ Version, Size } 之后紧跟 STORAGE_PROTOCOL_SPECIFIC_DATA
        var dataOffset = headerSize + ReadI32(output, 8 + 16);
        if (dataOffset + logSize > output.Length || dataOffset + logSize > returned)
            return new NvmeHealth(false, 0, 0, 0, 0, 0, $"bad protocol offset {dataOffset}, returned={returned}");

        var critical = output[dataOffset];
        var tempK = BitConverter.ToUInt16(output, dataOffset + 1);
        var used = output[dataOffset + 5];
        var units = ReadU128Low(output, dataOffset + 48); // Data Units Written 位于 48..63，128 位小端
        return new NvmeHealth(true, 0, critical, tempK, used, units, "ok");
    }

    // ---------------- 盘符映射 ----------------

    private static void ProbeVolumes(Report r)
    {
        r.Section("volume -> disk mapping");

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;

            var letter = d.Name.TrimEnd('\\'); // 例如 C:
            using var h = Native.CreateFileW(VolPrefix + letter, 0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (h.IsInvalid)
            {
                r.Line($"{letter,-4} -> FAIL win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            var output = new byte[1024];
            if (!Native.DeviceIoControl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, Array.Empty<byte>(), 0,
                    output, output.Length, out var returned, IntPtr.Zero) || returned < 8)
            {
                r.Line($"{letter,-4} -> FAIL win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            var count = BitConverter.ToUInt32(output, 0);
            var disks = new List<int>();
            for (var i = 0; i < count && 8 + (i + 1) * 24 <= returned; i++)
                disks.Add(BitConverter.ToInt32(output, 8 + i * 24));

            r.Line($"{letter,-4} -> disk {string.Join(",", disks)}   ({d.VolumeLabel}, {FormatBytes(d.TotalSize)})");
        }
    }

    // ---------------- 进程 ----------------

    private static void ProbeProcesses(Report r)
    {
        r.Section("process write counters");

        var swEnum = Stopwatch.StartNew();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (Exception ex)
        {
            r.Line("GetProcesses failed: " + ex.Message);
            return;
        }
        swEnum.Stop();

        var sw = Stopwatch.StartNew();
        var opened = 0;
        var failed = 0;
        ulong sum = 0;
        var rows = new List<(string Name, int Pid, ulong Bytes)>();
        var errors = new Dictionary<int, int>();
        var fallbackOk = 0;

        foreach (var pr in procs)
        {
            string name;
            try { name = pr.ProcessName; } catch { name = "?"; }
            var pid = pr.Id;

            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid)
            {
                var e1 = Marshal.GetLastWin32Error();
                h.Dispose();

                // 换个权限再试一次，确认不是"权限类型不对"而是"真的拒绝"
                h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, pid);
                if (h.IsInvalid)
                {
                    failed++;
                    var e = Marshal.GetLastWin32Error();
                    errors[e] = errors.TryGetValue(e, out var c) ? c + 1 : 1;
                    h.Dispose();
                    _ = e1;
                    continue;
                }
                fallbackOk++;
            }

            opened++;
            if (Native.GetProcessIoCounters(h, out var io))
            {
                sum += io.WriteTransferCount;
                if (io.WriteTransferCount > 0) rows.Add((name, pid, io.WriteTransferCount));
            }
            h.Dispose();
        }
        sw.Stop();

        r.Line($"GetProcesses()      : {procs.Length} processes in {swEnum.ElapsedMilliseconds} ms");
        r.Line($"counter loop        : {sw.ElapsedMilliseconds} ms for {procs.Length} processes");
        r.Line($"opened              : {opened}");
        r.Line($"open failed         : {failed}");
        r.Line($"needed QUERY_INFO   : {fallbackOk}   (LIMITED 被拒、降级到 QUERY_INFORMATION 才成功)");
        if (errors.Count > 0)
            r.Line("failure win32 codes : " + string.Join(", ", errors.Select(kv => $"{kv.Key}x{kv.Value}")));
        r.Line($"sum WriteTransfer   : {FormatBytes(sum)}  ({sum} bytes)");
        r.Line();
        r.Line("top 10 by WriteTransferCount:");
        foreach (var row in rows.OrderByDescending(x => x.Bytes).Take(10))
            r.Line($"  {row.Bytes,15:N0}  {row.Name} ({row.Pid})");

        r.Section("per-process vs firmware (口径差距)");
        r.Line("进程 WriteTransferCount 之和 = " + FormatBytes(sum));
        r.Line("对比上面 physical drives 里的 firmware total，判断两者量级关系。");
        r.Line("注意：WriteTransferCount 含管道/套接字/控制台写入，且写回缓存会放大，");
        r.Line("通常远大于固件落盘量。这个差距是本机实测值，写进契约前要用它校准界面文案。");
    }

    // ---------------- SATA SMART 多方案诊断 ----------------

    private static void ProbeSata(Report r)
    {
        r.Section("SATA SMART variants");

        for (var i = 0; i < 4; i++)
        {
            var path = PhysicalDrivePrefix + i;
            using var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (h.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == 2) break;
                r.Line($"[{path}] open RW FAIL win32={err}");
                continue;
            }

            r.Line($"[{path}] open RW OK  sector={ReadSectorSize(h)}");

            var a = SmartRecv(h, bigInput: true);
            r.Line($"  A smart_recv(544B) : {a.Status}");
            if (a.Data is not null) DumpAttributes(r, a.Data, 0, "A");

            var b = SmartRecv(h, bigInput: false);
            r.Line($"  B smart_recv(36B)  : {b.Status}");
            if (b.Data is not null) DumpAttributes(r, b.Data, 0, "B");

            var c = AtaPassThrough(h);
            r.Line($"  C ata_passthrough  : {c.Status}");
            if (c.Data is not null) DumpAttributes(r, c.Data, 0, "C");

            var d = PredictFailure(h);
            r.Line($"  D predict_failure  : {d.Status}");
            if (d.Data is not null) DumpAttributes(r, d.Data, 4, "D");
        }
    }

    private static (string Status, byte[]? Data) SmartRecv(SafeFileHandle h, bool bigInput)
    {
        const int dataSize = 512;
        var input = new byte[bigInput ? 32 + dataSize : 36];
        WriteI32(input, 0, dataSize); // cBufferSize
        input[4] = 0xD0;              // bFeaturesReg = SMART READ ATTRIBUTES
        input[5] = 0x01;              // bSectorCountReg
        input[6] = 0x01;              // bSectorNumberReg
        input[7] = 0xC2;              // bCylHighReg
        input[8] = 0x4F;              // bCylLowReg
        input[9] = 0xA0;              // bDriveHeadReg
        input[10] = 0xB0;             // bCommandReg = SMART
        input[12] = 0x00;             // bDriveNumber

        var output = new byte[16 + dataSize];
        if (!Native.DeviceIoControl(h, Native.IOCTL_SMART_RCV_DRIVE_DATA, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero))
        {
            return ($"FAIL win32={Marshal.GetLastWin32Error()} returned={returned}", null);
        }

        var driverError = output[4];
        var ideError = output[5];
        if (driverError != 0 || ideError != 0)
            return ($"REJECTED driver={driverError} ide=0x{ideError:X2} returned={returned}", null);

        var data = new byte[dataSize];
        Array.Copy(output, 16, data, 0, dataSize);
        return ($"OK returned={returned}", data);
    }

    private static (string Status, byte[]? Data) AtaPassThrough(SafeFileHandle h)
    {
        const int dataSize = 512;
        const int headerSize = 48; // ATA_PASS_THROUGH_DIRECT, x64 布局

        var buffer = new byte[headerSize];
        var data = Marshal.AllocHGlobal(dataSize);

        try
        {
            for (var i = 0; i < dataSize; i++) Marshal.WriteByte(data, i, 0);

            buffer[0] = headerSize;
            buffer[2] = 0x03;                     // AtaFlags: DRDY_REQUIRED | DATA_IN
            WriteI32(buffer, 8, dataSize);         // DataTransferLength
            WriteI32(buffer, 12, 10);              // TimeOutValue
            Buffer.BlockCopy(BitConverter.GetBytes(data.ToInt64()), 0, buffer, 24, 8); // DataBuffer

            // CurrentTaskFile 起于 offset 40：
            // features, sectorCount, sectorNumber, cylLow, cylHigh, driveHead, command, reserved
            buffer[40] = 0xD0;
            buffer[41] = 0x01;
            buffer[42] = 0x01;
            buffer[43] = 0x4F;
            buffer[44] = 0xC2;
            buffer[45] = 0xA0;
            buffer[46] = 0xB0;

            if (!Native.DeviceIoControl(h, Native.IOCTL_ATA_PASS_THROUGH_DIRECT, buffer, buffer.Length,
                    buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                return ($"FAIL win32={Marshal.GetLastWin32Error()} returned={returned}", null);
            }

            var result = new byte[dataSize];
            Marshal.Copy(data, result, 0, dataSize);
            return ($"OK returned={returned}", result);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static (string Status, byte[]? Data) PredictFailure(SafeFileHandle h)
    {
        var output = new byte[4 + 512];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_PREDICT_FAILURE, Array.Empty<byte>(), 0,
                output, output.Length, out var returned, IntPtr.Zero))
        {
            return ($"FAIL win32={Marshal.GetLastWin32Error()} returned={returned}", null);
        }

        var data = new byte[512];
        Array.Copy(output, 4, data, 0, 512);
        return ($"OK returned={returned} predictFail={BitConverter.ToInt32(output, 0)}", data);
    }

    private static void DumpAttributes(Report r, byte[] data, int baseOffset, string tag)
    {
        var start = baseOffset + 2;
        var seen = 0;

        for (var i = 0; i < 30; i++)
        {
            var b = start + i * 12;
            if (b + 12 > data.Length) break;

            var id = data[b];
            if (id == 0) continue;
            seen++;

            if (id is 5 or 9 or 194 or 231 or 241 or 246)
            {
                var raw = BitConverter.ToUInt64(new[]
                {
                    data[b + 5], data[b + 6], data[b + 7], data[b + 8],
                    data[b + 9], data[b + 10], (byte)0, (byte)0
                }, 0);
                r.Line($"      [{tag}] attr {id,3}  value={data[b + 3],3}  worst={data[b + 4],3}  raw={raw}");
            }
        }

        r.Line($"      [{tag}] 解析到 {seen} 条属性" + (seen == 0 ? "（数据可能不是 SMART 属性表）" : ""));
    }

    private static int ReadSectorSize(SafeFileHandle h)
    {
        var output = new byte[32];
        if (Native.DeviceIoControl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY, Array.Empty<byte>(), 0,
                output, output.Length, out var returned, IntPtr.Zero) && returned >= 24)
        {
            var size = BitConverter.ToInt32(output, 20);
            if (size > 0) return size;
        }
        return 512;
    }

    // ---------------- 工具 ----------------

    private static void WriteI32(byte[] buf, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buf, offset);

    private static int ReadI32(byte[] buf, int offset) =>
        BitConverter.ToInt32(buf, offset);

    private static ulong ReadU128Low(byte[] buf, int offset)
    {
        var low = BitConverter.ToUInt64(buf, offset);
        var high = BitConverter.ToUInt64(buf, offset + 8);
        return high == 0 ? low : ulong.MaxValue; // 溢出到 128 位高位时标记饱和（实际不会发生）
    }

    private static string ReadAnsiZ(byte[] buf, int offset)
    {
        var end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    private static string DescribeBus(uint bus) => bus switch
    {
        0 => "unknown(0)",
        1 => "SCSI(1)",
        2 => "ATAPI(2)",
        3 => "ATA(3)",
        4 => "1394(4)",
        5 => "SSA(5)",
        6 => "Fibre(6)",
        7 => "USB(7)",
        8 => "RAID(8)",
        9 => "iSCSI(9)",
        10 => "SAS(10)",
        11 => "SATA(11)",
        12 => "SD(12)",
        13 => "MMC(13)",
        14 => "Virtual(14)",
        15 => "FileBackedVirtual(15)",
        16 => "Spaces(16)",
        17 => "NVMe(17)",
        18 => "SCM(18)",
        19 => "UFS(19)",
        _ => $"bus({bus})"
    };

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1e12) return (bytes / 1e12).ToString("F3") + " TB";
        if (bytes >= 1e9) return (bytes / 1e9).ToString("F2") + " GB";
        if (bytes >= 1e6) return (bytes / 1e6).ToString("F1") + " MB";
        if (bytes >= 1e3) return (bytes / 1e3).ToString("F0") + " KB";
        return bytes.ToString("F0") + " B";
    }
}
