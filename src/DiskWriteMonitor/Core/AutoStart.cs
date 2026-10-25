using Microsoft.Win32;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 开机自启。契约 §7 要求用 HKCU 的 Run 键。
/// 这里必须配合 asInvoker 运行：如果整个进程强制提权，登录时会被 UAC 拦下来，自启等于失效。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiskWriteMonitor";

    public static string ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? string.Empty : path;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExePath)) return false;
                key.SetValue(ValueName, "\"" + ExePath + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }
}
