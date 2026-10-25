using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static DiskWriteMonitor.Core.Native;

namespace DiskWriteMonitor.Core;

public enum DiskHealthStatus
{
    Ok,
    NoPermission,
    NotSupported,
    Failed
}

public sealed class DiskHealth
{
    public DiskHealthStatus Status { get; init; }

    /// <summary>
    /// 固件记录的累计写入字节数（出厂至今）。
    /// NVMe 是可靠的；SATA 要看厂商，有些便宜盘把 241 记成 1GB 为单位，所以另有 FirmwareReliable 标记。
    /// </summary>
    public ulong TotalBytes { get; init; }

    /// <summary>固件累计值是否可信。不可信时界面显示「—」，不显示假数字。</summary>
    public bool FirmwareReliable { get; init; }

    public byte PercentageUsed { get; init; }
    public int TemperatureC { get; init; } = -1;
    public byte CriticalWarning { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class DiskInfo
{
    public int Number { get; init; }
    public string Path => $@"\\.\PhysicalDrive{Number}";
    public string Model { get; init; } = string.Empty;
    public uint BusType { get; init; }
    public bool HintNvme { get; init; }
    public string Letters { get; set; } = string.Empty;

    public bool IsNvme => BusType == BusTypeNvme;

    public string BusName => BusType == 0 ? "硬盘" : DescribeBus(BusType);

    public string Identifier
    {
        get
        {
            var parts = new List<string>();
            if (BusType != 0) parts.Add(BusName);
            if (!string.IsNullOrEmpty(Letters)) parts.Add("含 " + Letters);
            return parts.Count == 0 ? Path : string.Join("，", parts);
        }
    }

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Model)) return Model;
            return HintNvme ? $"物理磁盘 {Number}（NVMe）" : $"物理磁盘 {Number}";
        }
    }
}

/// <summary>
/// 盘级写入量。两条数据通路：
///   NVMe → 健康日志（IOCTL_STORAGE_QUERY_PROPERTY，需要读权限 = 管理员）
///   SATA → SMART READ DATA（IOCTL_SMART_RCV_DRIVE_DATA，需要读写权限 = 管理员）
/// 机械硬盘两者都没有等价指标，明确返回"不支持"，不要显示 0。
/// </summary>
public static class SmartReader
{
    private const double NvmeUnitBytes = 512_000.0; // 1 Data Unit = 1000 × 512 B

    public static List<DiskInfo> Enumerate()
    {
        var letters = MapVolumesToDisks();
        var list = new List<DiskInfo>();

        for (var i = 0; i < 16; i++)
        {
            var path = @"\\.\PhysicalDrive" + i;

            using var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            var err = Marshal.GetLastWin32Error();
            if (h.IsInvalid && err == ErrorFileNotFound) break; // 后面没有更多磁盘了

            // 没有读权限时退回零权限句柄，拿到多少算多少
            SafeFileHandle handle = h;
            if (handle.IsInvalid)
            {
                handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (handle.IsInvalid) continue;
            }

            try
            {
                var model = ReadModel(handle);
                var bus = ReadBusType(handle);
                var hint = ReadEnumHint(i, out var enumNvme);

                if (string.IsNullOrWhiteSpace(model)) model = hint;

                list.Add(new DiskInfo
                {
                    Number = i,
                    Model = model,
                    BusType = bus,
                    HintNvme = enumNvme,
                    Letters = letters.TryGetValue(i, out var l) ? l : string.Empty
                });
            }
            finally
            {
                handle.Dispose();
            }
        }

        return list;
    }

    public static DiskHealth Read(DiskInfo disk)
    {
        DiskHealthStatus? denyStatus = null;
        var messages = new List<string>();

        // ① NVMe 优先。总线类型未知时（未提权读不到）也试一次，失败自然落下去。
        if (disk.IsNvme || disk.HintNvme || disk.BusType == 0)
        {
            using var h = CreateFileW(disk.Path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (h.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorAccessDenied) denyStatus = DiskHealthStatus.NoPermission;
                else messages.Add($"打开设备失败（错误 {err}）");
            }
            else
            {
                var nvme = QueryNvmeHealth(h);
                if (nvme.Status == DiskHealthStatus.Ok) return nvme;
                if (nvme.Status == DiskHealthStatus.NoPermission) denyStatus = DiskHealthStatus.NoPermission;
                else if (!string.IsNullOrEmpty(nvme.Message)) messages.Add(nvme.Message);
            }
        }

        // ② SATA / ATA：SMART READ DATA
        using (var h = CreateFileW(disk.Path, GENERIC_READ | GENERIC_WRITE,
                   FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                   OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
        {
            if (h.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorAccessDenied) denyStatus = DiskHealthStatus.NoPermission;
                else messages.Add($"以读写方式打开设备失败（错误 {err}）");
            }
            else
            {
                var sata = ReadSata(h);
                if (sata.Status == DiskHealthStatus.Ok) return sata;
                if (sata.Status == DiskHealthStatus.NoPermission) denyStatus = DiskHealthStatus.NoPermission;
                else if (!string.IsNullOrEmpty(sata.Message)) messages.Add(sata.Message);
            }
        }

        if (denyStatus == DiskHealthStatus.NoPermission)
        {
            return new DiskHealth
            {
                Status = DiskHealthStatus.NoPermission,
                Message = "需要管理员权限才能读取硬盘数据"
            };
        }

        // 两条路都拿不到「累计写入」这个指标，而不是出错
        if (messages.Any(m => m.Contains("不支持") || m.Contains("找不到") || m.Contains("不提供")))
        {
            return new DiskHealth
            {
                Status = DiskHealthStatus.NotSupported,
                Message = messages.First(m => m.Contains("不支持") || m.Contains("找不到") || m.Contains("不提供"))
            };
        }

        return new DiskHealth
        {
            Status = DiskHealthStatus.Failed,
            Message = messages.Count > 0 ? messages[0] : "读不到这块硬盘的写入数据"
        };
    }

    // ---------------- NVMe ----------------

    private static DiskHealth QueryNvmeHealth(SafeFileHandle h)
    {
        const int headerSize = 8;  // STORAGE_PROPERTY_QUERY
        const int protoSize = 40;  // STORAGE_PROTOCOL_SPECIFIC_DATA
        const int logSize = 512;   // NVME_HEALTH_INFO_LOG

        var input = new byte[headerSize + protoSize];
        WriteI32(input, 0, 49); // StorageDeviceProtocolSpecificProperty
        WriteI32(input, 4, 0);  // PropertyStandardQuery
        const int p = headerSize;
        WriteI32(input, p + 0, 3);          // ProtocolTypeNvme
        WriteI32(input, p + 4, 2);          // NVMeDataTypeLogPage
        WriteI32(input, p + 8, 0x02);       // NVME_LOG_PAGE_HEALTH_INFO
        WriteI32(input, p + 16, protoSize); // ProtocolDataOffset
        WriteI32(input, p + 20, logSize);   // ProtocolDataLength

        var output = new byte[headerSize + protoSize + logSize + 64];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            return new DiskHealth
            {
                Status = err == ErrorAccessDenied ? DiskHealthStatus.NoPermission : DiskHealthStatus.Failed,
                Message = err == ErrorAccessDenied ? "需要管理员权限" : $"NVMe 健康日志读取失败（错误 {err}）"
            };
        }

        var dataOffset = headerSize + ReadI32(output, 8 + 16);
        if (dataOffset + logSize > output.Length || dataOffset + logSize > returned)
        {
            return new DiskHealth
            {
                Status = DiskHealthStatus.Failed,
                Message = "NVMe 健康日志结构异常"
            };
        }

        var units = BitConverter.ToUInt64(output, dataOffset + 48);
        var tempK = BitConverter.ToUInt16(output, dataOffset + 1);

        return new DiskHealth
        {
            Status = DiskHealthStatus.Ok,
            TotalBytes = (ulong)(units * NvmeUnitBytes),
            FirmwareReliable = true,
            PercentageUsed = output[dataOffset + 5],
            TemperatureC = tempK >= 273 ? tempK - 273 : tempK,
            CriticalWarning = output[dataOffset],
            Message = "ok"
        };
    }

    // ---------------- SATA ----------------

    /// <summary>
    /// 读 SMART 属性表。属性 241 = Total_LBAs_Written（累计写入 LBA 数），
    /// 乘以扇区大小就是累计字节数。这是 SATA 固态唯一可靠的写入量来源。
    /// </summary>
    private static DiskHealth ReadSata(SafeFileHandle h)
    {
        // 实测：这块 SATA 盘挂在 AHCI/SCSI 栈上，SMART_RCV_DRIVE_DATA 返回 1117（不支持），
        // 只有 ATA_PASS_THROUGH_DIRECT 能通。所以 SATA 一律走直通。
        var (ok, message, data) = AtaPassThrough(h);
        if (!ok)
        {
            return new DiskHealth
            {
                Status = message.Contains("被拒") ? DiskHealthStatus.NoPermission : DiskHealthStatus.Failed,
                Message = message
            };
        }

        ulong? totalLbas = null;
        int? temperature = null;
        byte? lifeLeft = null;

        for (var i = 0; i < 30; i++)
        {
            var b = 2 + i * 12;
            if (b + 12 > data.Length) break;

            var id = data[b];
            if (id == 0) continue;

            var raw = new byte[6];
            Array.Copy(data, b + 5, raw, 0, 6);

            switch (id)
            {
                case 241: // Total LBAs Written
                    totalLbas = ReadLba48(raw);
                    break;
                case 246: // 部分厂商把累计写入放在 246
                    totalLbas ??= ReadLba48(raw);
                    break;
                case 194:
                case 190: // 温度，raw 第一个字节
                    temperature ??= raw[0] > 0 ? raw[0] : null;
                    break;
                case 231: // SSD Life Left，用 100 - 剩余 估百分比
                    lifeLeft ??= data[b + 3];
                    break;
            }
        }

        var sector = ReadSectorSize(h);

        // 判定固件计数是否可信。
        // 标准 SATA 盘的 241 记的是 512 字节 LBA，一颗用过一段时间的盘不可能只写了几百 MB。
        // 实测这台机器的 Fanxiang 盘 241 = 24197，但寿命已用掉 12%，说明它是以 1GB 为单位的，
        // 这种计数器粒度太粗（每秒采样几乎永远不变），不能拿来算速率。
        var firmwareBytes = totalLbas is null ? 0UL : totalLbas.Value * (ulong)sector;
        var reliable = firmwareBytes >= MinPlausibleFirmwareBytes;

        return new DiskHealth
        {
            Status = DiskHealthStatus.Ok,
            TotalBytes = reliable ? firmwareBytes : 0,
            FirmwareReliable = reliable,
            PercentageUsed = lifeLeft is null ? (byte)0 : (byte)Math.Clamp(100 - lifeLeft.Value, 0, 100),
            TemperatureC = temperature ?? -1,
            Message = reliable ? "ok" : "该硬盘固件不提供可靠的累计写入量"
        };
    }

    /// <summary>固件累计写入低于 1 GiB 视为不可信（正常用过的盘不可能这么少）</summary>
    private const ulong MinPlausibleFirmwareBytes = 1UL << 30;
    /// <summary>
    /// ATA PASS THROUGH（DIRECT）读 SMART 属性表。
    /// 相比老的 SMART_RCV_DRIVE_DATA，这条路在 AHCI/SCSI 栈上也能用。
    /// </summary>
    private static (bool Ok, string Message, byte[] Data) AtaPassThrough(SafeFileHandle h)
    {
        const int dataSize = 512;
        const int headerSize = 48; // ATA_PASS_THROUGH_DIRECT 在 x64 下是 48 字节

        var buffer = new byte[headerSize];
        var data = Marshal.AllocHGlobal(dataSize);

        try
        {
            for (var i = 0; i < dataSize; i++) Marshal.WriteByte(data, i, 0);

            buffer[0] = headerSize;                        // Length
            buffer[2] = 0x03;                              // AtaFlags: DRDY_REQUIRED | DATA_IN
            WriteI32(buffer, 8, dataSize);                 // DataTransferLength
            WriteI32(buffer, 12, 10);                      // TimeOutValue
            Buffer.BlockCopy(BitConverter.GetBytes(data.ToInt64()), 0, buffer, 24, 8); // DataBuffer

            // CurrentTaskFile 起于 offset 40：
            // features, sectorCount, sectorNumber, cylLow, cylHigh, driveHead, command, reserved
            buffer[40] = 0xD0; // SMART READ ATTRIBUTES
            buffer[41] = 0x01;
            buffer[42] = 0x01;
            buffer[43] = 0x4F; // cyl low
            buffer[44] = 0xC2; // cyl high
            buffer[45] = 0xA0; // drive/head
            buffer[46] = 0xB0; // SMART

            if (!DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH_DIRECT, buffer, buffer.Length,
                    buffer, buffer.Length, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                return (false, err == ErrorAccessDenied
                    ? "SMART 读取被拒（需要管理员权限）"
                    : $"ATA_PASS_THROUGH 失败（错误 {err}）", Array.Empty<byte>());
            }

            var result = new byte[dataSize];
            Marshal.Copy(data, result, 0, dataSize);
            return (true, "ok", result);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static ulong ReadLba48(byte[] raw)
    {
        ulong value = 0;
        for (var i = 5; i >= 0; i--) value = (value << 8) | raw[i];
        return value;
    }

    private static int ReadSectorSize(SafeFileHandle h)
    {
        var output = new byte[32];
        if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, Array.Empty<byte>(), 0,
                output, output.Length, out var returned, IntPtr.Zero) && returned >= 24)
        {
            var size = BitConverter.ToInt32(output, 20);
            if (size > 0) return size;
        }
        return 512;
    }

    // ---------------- 设备信息 ----------------

    private static string ReadModel(SafeFileHandle h)
    {
        var input = new byte[8];
        WriteI32(input, 0, 0); // StorageDeviceProperty
        var output = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero) || returned < 36)
            return string.Empty;

        return ReadAnsiZ(output, ReadI32(output, 16)); // ProductIdOffset
    }

    private static uint ReadBusType(SafeFileHandle h)
    {
        var input = new byte[8];
        WriteI32(input, 0, 0);
        var output = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out var returned, IntPtr.Zero) || returned < 32)
            return 0;
        return BitConverter.ToUInt32(output, 28); // STORAGE_DEVICE_DESCRIPTOR.BusType
    }

    /// <summary>
    /// 未提权时 IOCTL 拿不到型号，退一步读注册表：disk\Enum 下的 "0"/"1" 就是物理磁盘号，
    /// 值是设备实例路径（NVME\... 或 SCSI\DISK&amp;VEN_xxx&amp;PROD_yyy...）。
    /// </summary>
    private static string ReadEnumHint(int diskNumber, out bool looksNvme)
    {
        looksNvme = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
            if (key?.GetValue(diskNumber.ToString()) is not string raw || string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            looksNvme = raw.StartsWith("NVME", StringComparison.OrdinalIgnoreCase);

            var parts = raw.Split('\\');
            if (parts.Length < 2) return string.Empty;

            string? vendor = null;
            string? product = null;
            foreach (var token in parts[1].Split('&'))
            {
                if (token.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase)) vendor = token[4..];
                else if (token.StartsWith("PROD_", StringComparison.OrdinalIgnoreCase)) product = token[5..];
            }

            var name = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return name.Replace('_', ' ').Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<int, string> MapVolumesToDisks()
    {
        var map = new Dictionary<int, List<string>>();

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            var letter = d.Name.TrimEnd('\\');

            using var h = CreateFileW(@"\\.\" + letter, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h.IsInvalid) continue;

            var output = new byte[1024];
            if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, Array.Empty<byte>(), 0,
                    output, output.Length, out var returned, IntPtr.Zero) || returned < 8)
                continue;

            var count = BitConverter.ToUInt32(output, 0);
            for (var i = 0; i < count && 8 + (i + 1) * 24 <= returned; i++)
            {
                var disk = BitConverter.ToInt32(output, 8 + i * 24);
                if (!map.TryGetValue(disk, out var l)) map[disk] = l = new List<string>();
                l.Add(letter);
            }
        }

        return map.ToDictionary(kv => kv.Key, kv => string.Join("、", kv.Value));
    }
}
