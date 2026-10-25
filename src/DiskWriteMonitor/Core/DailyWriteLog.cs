using System.IO;
using System.Text;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 每日写入量流水：每天每块盘一行，追加到 daily-writes.csv。
///
/// 为什么需要它：SATA 固态的固件累计写入值不一定可信（廉价盘常把 SMART 241 记成别的单位），
/// 拿不到「出厂至今总量」时，就只能靠长期观测推算真实的日均写入。
/// 挂一两周后，用 gb 列的平均值就能估算这块盘还能撑多久。
///
/// 注意：数据只统计**本工具运行期间**观测到的增量，关机/退出期间不计。
/// 所以每行都带 monitoredHours，别把"跑了 3 小时的量"当成"一整天"。
/// </summary>
public static class DailyWriteLog
{
    private const string Header = "date,disk,model,letters,bytes,gb,monitoredHours";
    private const int MaxRows = 5000;

    public sealed class Row
    {
        public string Date = string.Empty;
        public int Disk;
        public string Model = string.Empty;
        public string Letters = string.Empty;
        public long Bytes;
        public double Hours;
    }

    /// <summary>CSV 位置跟随设置里的数据目录，默认在 %LOCALAPPDATA%\DiskWriteMonitor</summary>
    public static string FilePath => Path.Combine(AppSettings.ResolvedDataDir, "daily-writes.csv");

    public static List<Row> Load()
    {
        var rows = new List<Row>();
        try
        {
            if (!File.Exists(FilePath)) return rows;

            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 7) continue;
                if (!int.TryParse(parts[1], out var disk)) continue;
                if (!long.TryParse(parts[4], out var bytes)) continue;
                if (!double.TryParse(parts[6], System.Globalization.CultureInfo.InvariantCulture, out var hours)) continue;

                rows.Add(new Row
                {
                    Date = parts[0],
                    Disk = disk,
                    Model = parts[2],
                    Letters = parts[3],
                    Bytes = bytes,
                    Hours = hours
                });
            }
        }
        catch
        {
            // 文件坏了就当空的，不要让历史数据阻塞监控
        }

        return rows;
    }

    /// <summary>同一天同一块盘只保留一行（覆盖更新），写入用临时文件替换，避免半截文件。</summary>
    public static void Upsert(IEnumerable<Row> incoming)
    {
        try
        {
            var rows = Load();

            foreach (var row in incoming)
            {
                rows.RemoveAll(r => r.Date == row.Date && r.Disk == row.Disk);
                rows.Add(row);
            }

            rows = rows.OrderBy(r => r.Date, StringComparer.Ordinal)
                       .ThenBy(r => r.Disk)
                       .TakeLast(MaxRows)
                       .ToList();

            Directory.CreateDirectory(AppSettings.ResolvedDataDir);

            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var r in rows)
            {
                sb.Append(r.Date).Append(',')
                  .Append(r.Disk).Append(',')
                  .Append(Escape(r.Model)).Append(',')
                  .Append(Escape(r.Letters)).Append(',')
                  .Append(r.Bytes).Append(',')
                  .Append((r.Bytes / 1024.0 / 1024 / 1024).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .AppendLine(r.Hours.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(true));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // 写不进去不影响监控本身
        }
    }

    /// <summary>字段里若有逗号会破坏 CSV，替换成中文顿号</summary>
    private static string Escape(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace(',', '、');
}
