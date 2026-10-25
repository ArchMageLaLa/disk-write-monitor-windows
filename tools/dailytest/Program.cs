using DiskWriteMonitor.Core;

namespace DiskWriteMonitor.Core
{
    /// <summary>测试替身：把 AppSettings.Dir 指向临时目录，避免污染真实数据</summary>
    public static class AppSettings
    {
        internal static string Dir =>
            Environment.GetEnvironmentVariable("DAILY_TEST_DIR")
            ?? Path.Combine(Path.GetTempPath(), "dwm-dailytest");
    }
}

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("输出目录: " + DailyWriteLog.FilePath);

        // 第一天：两块盘
        DailyWriteLog.Upsert(new[]
        {
            new DailyWriteLog.Row { Date = "2026-10-25", Disk = 0, Model = "Fanxiang S100PRO", Letters = "C:、D:", Bytes = 73_014_687_744, Hours = 12.5 },
            new DailyWriteLog.Row { Date = "2026-10-25", Disk = 1, Model = "NVMe Asgard AN2 1TNVMe-M.2/80", Letters = "E:", Bytes = 1_073_741_824, Hours = 12.5 }
        });

        // 同一天再写一次：应当覆盖而不是追加
        DailyWriteLog.Upsert(new[]
        {
            new DailyWriteLog.Row { Date = "2026-10-25", Disk = 0, Model = "Fanxiang S100PRO", Letters = "C:、D:", Bytes = 80_000_000_000, Hours = 14.0 }
        });

        // 第二天：只跑了一块盘
        DailyWriteLog.Upsert(new[]
        {
            new DailyWriteLog.Row { Date = "2026-10-26", Disk = 0, Model = "Fanxiang S100PRO", Letters = "C:、D:", Bytes = 41_943_040, Hours = 2.0 }
        });

        Console.WriteLine("--- 文件内容 ---");
        foreach (var line in File.ReadAllLines(DailyWriteLog.FilePath))
            Console.WriteLine(line);

        var rows = DailyWriteLog.Load();
        Console.WriteLine($"--- 解析回读: {rows.Count} 行 ---");
        foreach (var r in rows)
            Console.WriteLine($"  {r.Date} disk{r.Disk} {r.Bytes / 1024.0 / 1024 / 1024:F2}GB {r.Hours}h {r.Model} [{r.Letters}]");

        var today = rows.Where(r => r.Date == "2026-10-25").ToList();
        var ok = today.Count == 2
                 && today.First(r => r.Disk == 0).Bytes == 80_000_000_000
                 && today.First(r => r.Disk == 1).Bytes == 1_073_741_824
                 && rows.Count == 3;

        Console.WriteLine(ok ? "PASS：同日覆盖生效，跨天保留" : "FAIL：行为不符合预期");
        return ok ? 0 : 1;
    }
}
