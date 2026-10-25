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

    /// <summary>
    /// PDH_RAW_COUNTER_ITEM_W 在 x64 下的布局：
    ///   szName            offset 0   (8)
    ///   PDH_RAW_COUNTER   offset 8
    ///     CStatus         +0  (4)
    ///     FILETIME        +4  (8)  -> 到 +12
    ///     FirstValue      +16 (8)  -> 但因为 LONGLONG 要 8 字节对齐，实际在 +16，
    ///                                 合起来就是 item 内的 offset 24
    ///     SecondValue     +24
    ///     MultiCount      +32
    ///   PDH_RAW_COUNTER 总长 40（含尾部对齐），所以 item 步长 = 8 + 40 = 48
    /// 上一版把 FirstValue 写成 offset 16（即 TimeStamp 区域），读出来自然是垃圾。
    /// </summary>
    private const int RawItemStride = 48;
    private const int RawFirstValueOffset = 24;

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
    /// 一次采集同时给出两套数据：
    ///   cumulative —— 自开机累计写入字节（raw FirstValue，精确，做差即得速率和会话量）
    ///   rates      —— PDH 算好的字节/秒（raw 读失败时兜底）
    /// </summary>
    public bool TryReadAll(out Dictionary<int, long> cumulative, out Dictionary<int, double> rates)
    {
        cumulative = new Dictionary<int, long>();
        rates = new Dictionary<int, double>();
        if (!_ready) return false;
        if (PdhCollectQueryData(_query) != 0) return false;

        // ---- 原始累计字节（精确来源）----
        var rawSize = 0u;
        PdhGetRawCounterArrayW(_counter, ref rawSize, out _, IntPtr.Zero);
        if (rawSize > 0)
        {
            var rawBuffer = Marshal.AllocHGlobal((int)rawSize);
            try
            {
                if (PdhGetRawCounterArrayW(_counter, ref rawSize, out var rawCount, rawBuffer) == 0)
                {
                    for (var i = 0; i < rawCount; i++)
                    {
                        var item = rawBuffer + i * RawItemStride;
                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                        if (!TryParseDiskNumber(name, out var number)) continue;

                        var first = Marshal.ReadInt64(item, RawFirstValueOffset);
                        if (first > 0 && first < long.MaxValue / 2) cumulative[number] = first;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(rawBuffer);
            }
        }

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

        return cumulative.Count > 0 || rates.Count > 0;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetRawCounterArrayW(
        IntPtr counter, ref uint bufferSize, out uint itemCount, IntPtr buffer);

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
