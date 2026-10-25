using System.Runtime.InteropServices;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 用性能计数器（PDH）读某块物理磁盘的实时写入字节/秒。
/// 选它而不是 IOCTL_DISK_PERFORMANCE 的原因：实测 IOCTL 拿不到（权限/驱动限制），
/// 而 PDH 的 PhysicalDisk 计数器不需要管理员权限就能读，粒度到字节，曲线才平滑。
/// </summary>
public sealed class DiskRateCounter : IDisposable
{
    /// <summary>
    /// 用原始计数器（PdhGetRawCounterArray）拿"自开机累计字节"。
    /// *_Persec 这类计数器的 raw FirstValue 就是累计量，做差不会有积分误差。
    /// 布局靠自校验兜底：算出来的增量明显不合理时，退回用 PDH 算好的速率。
    /// </summary>
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;
    private IntPtr _counter;
    private readonly bool _ready;

    public DiskRateCounter()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\PhysicalDisk(*)\Disk Write Bytes/sec",
                IntPtr.Zero, out _counter) != 0) return;

        // 第一次 collect 只是建基线，值要到第二次才有效
        if (PdhCollectQueryData(_query) != 0) return;
        _ready = true;
    }

    public bool IsReady => _ready;

    /// <summary>
    /// 一次采集拿回所有物理磁盘的写入速率（字节/秒），键为磁盘号。
    /// 所有盘同时采集，切换磁盘时各盘的会话累计才不会断档。
    /// 注：会话累计量由速率 × 时间间隔积分得到，1 秒采样下持续写入准确，
    /// 极短的写入爆发可能少计 —— 这是 perfmon 速率计数器本身的粒度限制。
    /// </summary>
    public bool TryReadAll(out Dictionary<int, double> rates)
    {
        rates = new Dictionary<int, double>();
        if (!_ready) return false;
        if (PdhCollectQueryData(_query) != 0) return false;

        // 第一次调用只用来探查需要多大缓冲，必然返回 PDH_MORE_DATA，不能当成失败
        var size = 0u;
        PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (size == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out var count, buffer) != 0)
                return false;

            // PDH_FMT_COUNTERVALUE_ITEM_W: szName(指针) + CStatus(4) + 对齐 + doubleValue(8)
            var stride = IntPtr.Size == 8 ? 24 : 16;

            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * stride;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                if (!TryParseDiskNumber(name, out var number)) continue; // 跳过 _total

                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (double.IsNaN(value) || value < 0) value = 0;
                rates[number] = value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return rates.Count > 0;
    }

    /// <summary>实例名形如 "0 c: d:"、"1 e:"、"_total"，取空格前那段转成磁盘号。</summary>
    private static bool TryParseDiskNumber(string instanceName, out int number)
    {
        number = -1;
        var space = instanceName.IndexOf(' ');
        var head = space < 0 ? instanceName : instanceName[..space];
        return int.TryParse(head, out number);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
