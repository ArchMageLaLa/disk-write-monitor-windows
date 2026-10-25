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

    /// <summary>
    /// 数据文件（daily-writes.csv）存放目录。空表示用默认的 %LOCALAPPDATA%\DiskWriteMonitor。
    /// 注意 state.json 本身始终留在默认位置——它就是这份配置，不能跟着搬。
    /// </summary>
    public string DataDir { get; set; } = string.Empty;

    internal static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskWriteMonitor");

    /// <summary>state.json 固定目录</summary>
    internal static string Dir => DefaultDir;

    /// <summary>当前生效的数据目录（CSV 写在这里）</summary>
    internal static string ResolvedDataDir { get; private set; } = DefaultDir;

    /// <summary>把配置里的路径解析成实际可用目录；不可用时退回默认值</summary>
    internal static void Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            ResolvedDataDir = DefaultDir;
            return;
        }

        try
        {
            Directory.CreateDirectory(configured);
            ResolvedDataDir = configured;
        }
        catch
        {
            // 盘符不存在 / 没权限，退回默认，不能让监控起不来
            ResolvedDataDir = DefaultDir;
        }
    }

    private static string FilePath => Path.Combine(Dir, "state.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    Resolve(loaded.DataDir);
                    return loaded;
                }
            }
        }
        catch
        {
            // 状态文件坏了不该影响启动，直接用默认值
        }

        Resolve(null);
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Resolve(DataDir);
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
