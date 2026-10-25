using System.IO;
using System.Text.Json;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 状态持久化。按契约要求放在 %LOCALAPPDATA%\DiskWriteMonitor\state.json，
/// 不写注册表，卸载时随目录一起删除。
/// </summary>
public sealed class AppSettings
{
    public DateTime? LastUpdateCheck { get; set; }
    public string? DismissedVersion { get; set; }
    public int SelectedDisk { get; set; } = -1;

    /// <summary>每日写入量流水的当前日期（yyyy-MM-dd）</summary>
    public string DailyDate { get; set; } = string.Empty;

    /// <summary>当天各磁盘累计写入字节（键为磁盘号字符串），跨天时结转到 CSV</summary>
    public Dictionary<string, long> DailyBytes { get; set; } = new();

    /// <summary>当天本工具实际运行的秒数，用于给流水行标注监控时长</summary>
    public double DailySeconds { get; set; }

    internal static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskWriteMonitor");

    private static string FilePath => Path.Combine(Dir, "state.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // 状态文件坏了不该影响启动，直接用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 存不下就算了，不影响监控功能
        }
    }
}
