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

    private static string Dir => Path.Combine(
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
