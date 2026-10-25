using System.Globalization;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 数值格式，严格按 WINDOWS-PORT.md §3.5：
/// ≥1GB 一位小数 GB / ≥10MB 整数 MB / ≥1MB 一位小数 MB / &lt;1MB 整数 KB
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= 1e9) return (bytes / 1e9).ToString("F1", Inv) + " GB";
        if (bytes >= 10e6) return (bytes / 1e6).ToString("F0", Inv) + " MB";
        if (bytes >= 1e6) return (bytes / 1e6).ToString("F1", Inv) + " MB";
        return (bytes / 1e3).ToString("F0", Inv) + " KB"; // <1MB 一律整数 KB，含 0
    }

    /// <summary>把 "12.3 GB" 拆成数字和单位，方便界面用不同字号排版。</summary>
    public static (string Value, string Unit) Split(string text)
    {
        var i = text.LastIndexOf(' ');
        return i < 0 ? (text, string.Empty) : (text[..i], text[(i + 1)..]);
    }

    /// <summary>MB/s，保留一位小数；量级大时只留整数，避免大数字跳动。</summary>
    public static (string Value, string Unit) Rate(double mbPerSec)
    {
        if (mbPerSec < 0) mbPerSec = 0;
        var text = mbPerSec >= 100 ? mbPerSec.ToString("F0", Inv)
                 : mbPerSec >= 10 ? mbPerSec.ToString("F0", Inv)
                 : mbPerSec.ToString("F1", Inv);
        return (text, "MB / 秒");
    }

    public static string Time(DateTime t) => t.ToString("HH:mm:ss", Inv);

    /// <summary>速率文本，带单位，用于"本次峰值"这类格子。</summary>
    public static string RateText(double mbPerSec)
    {
        if (mbPerSec <= 0) return "0 MB/s";
        var (value, _) = Rate(mbPerSec);
        return value + " MB/s";
    }
}
