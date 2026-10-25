using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using DiskWriteMonitor.Core;
using WinForms = System.Windows.Forms;

namespace DiskWriteMonitor;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private WinForms.NotifyIcon? _tray;
    private System.Threading.Timer? _dailyTimer;

    private AppSettings? _settings;
    private WriteMonitor? _monitor;
    private UpdateChecker? _updater;
    private MainWindow? _window;

    public static new App Current => (App)Application.Current;

    public UpdateChecker? Updater => _updater;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：第二个进程只负责把已有窗口叫到前台，然后自己退出
        _instanceMutex = new Mutex(true, @"Local\DiskWriteMonitor.Instance", out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(@"Local\DiskWriteMonitor.Activate").Set(); }
            catch { /* 对端还没建好事件，忽略 */ }
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DiskWriteMonitor.Activate");
        var listener = new Thread(() =>
        {
            while (true)
            {
                _activateEvent.WaitOne();
                Dispatcher.BeginInvoke(new Action(ShowMain));
            }
        })
        { IsBackground = true };
        listener.Start();

        _settings = AppSettings.Load();
        _monitor = new WriteMonitor(_settings);
        _updater = new UpdateChecker(_settings);

        _window = new MainWindow(_monitor, _updater);
        MainWindow = _window;

        // --disk <n>：启动时选中指定物理磁盘，便于验收多盘切换
        var diskIndex = Array.IndexOf(e.Args, "--disk");
        if (diskIndex >= 0 && diskIndex + 1 < e.Args.Length
            && int.TryParse(e.Args[diskIndex + 1], out var diskNumber))
        {
            _monitor.SelectDisk(diskNumber);
        }

        // --render <path>：跑几秒采样后把窗口渲染成 PNG，用于出图和验收
        var renderIndex = Array.IndexOf(e.Args, "--render");
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            var path = e.Args[renderIndex + 1];
            _window.Show();
            _monitor.Start();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RenderTo(path);
                Shutdown();
            };
            timer.Start();
            return;
        }

        // --dump <path> [seconds]：采样若干秒后导出进程行与合计，用于核对数字
        var dumpIndex = Array.IndexOf(e.Args, "--dump");
        if (dumpIndex >= 0 && dumpIndex + 1 < e.Args.Length)
        {
            var path = e.Args[dumpIndex + 1];
            var seconds = 5;
            if (dumpIndex + 2 < e.Args.Length && int.TryParse(e.Args[dumpIndex + 2], out var parsed))
                seconds = Math.Clamp(parsed, 2, 300);

            _window.Show();
            _monitor.Start();

            // --switch-test：中途切到磁盘 1 再切回磁盘 0，验证各盘会话量不会因为切换被清零
            if (e.Args.Contains("--switch-test") && _monitor.Disks.Count > 1)
            {
                var toOther = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                toOther.Tick += (_, _) =>
                {
                    toOther.Stop();
                    _monitor.SelectDisk(1);
                };
                toOther.Start();

                var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                back.Tick += (_, _) =>
                {
                    back.Stop();
                    _monitor.SelectDisk(0);
                };
                back.Start();
            }

            var dumpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            dumpTimer.Tick += (_, _) =>
            {
                dumpTimer.Stop();
                DumpRows(path);
                Shutdown();
            };
            dumpTimer.Start();
            return;
        }

        CreateTray();
        _window.Show();

        _monitor.Start();

        // 每天最多一次；这里用小时级 Timer 兜住"应用常年不重启"的情况
        _updater.CheckIfDue();
        _dailyTimer = new System.Threading.Timer(_ => _updater?.CheckIfDue(), null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private void DumpRows(string path)
    {
        try
        {
            var lines = new List<string>
            {
                "name\tbytes\tpercent\tbarWidthPx",
                "disk=" + _monitor?.DeviceTitle + " | " + _monitor?.DeviceSubtitle +
                "  chips=" + (_monitor?.DiskChips.Count ?? 0) +
                "  selector=" + _monitor?.ShowDiskSelector,
                "session=" + _monitor?.SessionBytes.ToString("F0") +
                "  sessionHuman=" + _monitor?.SessionText +
                "  rate=" + _monitor?.RateValue + " " + _monitor?.RateUnit +
                "  peak=" + _monitor?.PeakText +
                "  temp=" + _monitor?.TempText +
                "  total=" + _monitor?.TotalText,
                "rates(MB/s)=" + _monitor?.RateHistoryText,
                "disks=" + _monitor?.DiskDebugText
            };

            if (_monitor is not null)
            {
                foreach (var row in _monitor.Rows.Take(15))
                    lines.Add($"{row.Name}\t{row.Bytes}\t{row.Percent:F6}\t{row.Percent * 96:F1}");
            }

            // 核对绑定是否真的生效：直接读可视化树里进度条的实际宽度
            lines.Add("--- rendered bar widths ---");            if (_window is not null)
            {
                foreach (var d in Descendants(_window))
                {
                    if (d is System.Windows.Controls.Border { Tag: "barfill" } bar
                        && bar.DataContext is ProcRow row)
                    {
                        lines.Add($"{row.Name}\tactual={bar.ActualWidth:F1}\texpected={row.Percent * 96:F1}");
                    }
                }
            }

            System.IO.File.WriteAllLines(path, lines);
        }
        catch
        {
            // 导出失败不影响正常使用
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private void RenderTo(string path)
    {
        try
        {
            var w = _window;
            if (w is null) return;

            w.UpdateLayout();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);

            // 2 倍出图：细如 5px 的进度条在 1 倍图里看不清，验收时容易误判
            const double scale = 2.0;

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX * scale),
                (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY * scale),
                96 * dpi.DpiScaleX * scale,
                96 * dpi.DpiScaleY * scale,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(w);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            using var fs = System.IO.File.Create(path);
            encoder.Save(fs);
        }
        catch
        {
            // 出图失败不影响正常使用
        }
    }

    private void CreateTray()    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowMain());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "磁盘写入量",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    /// <summary>托盘图标直接从 exe 自身取，保证与任务栏/资源管理器里显示的一致。</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // 取不到就用系统默认图标，不能让托盘消失
        }

        return SystemIcons.Application;
    }

    /// <summary>关闭窗口只是收进托盘，采样继续（契约 §5）。</summary>
    public void HideToTray(bool firstTime)
    {
        _window?.Hide();

        if (firstTime && _tray is not null)
        {
            _tray.BalloonTipTitle = "磁盘写入量";
            _tray.BalloonTipText = "已收进托盘，仍在后台采样。右键托盘图标可以退出。";
            _tray.ShowBalloonTip(3000);
        }
    }

    public void ShowMain()
    {
        if (_window is null) return;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    public void ExitApp()
    {
        _window?.ForceClose();
        Shutdown();
    }

    /// <summary>以管理员身份重启自己（读 SMART 需要）。先放掉单实例锁，否则新进程会以为自己不是第一个。</summary>
    public void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            ReleaseInstanceLock();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            ExitApp();
        }
        catch
        {
            // 用户在 UAC 上点了"否"，保持当前实例继续跑
            RebuildInstanceLock();
        }
    }

    private void ReleaseInstanceLock()
    {
        try { _activateEvent?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        _activateEvent = null;
        _instanceMutex = null;
    }

    private void RebuildInstanceLock()
    {
        _instanceMutex = new Mutex(true, @"Local\DiskWriteMonitor.Instance", out _);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DiskWriteMonitor.Activate");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dailyTimer?.Dispose();
        _monitor?.Stop();
        _settings?.Save();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _instanceMutex?.Dispose();
        _activateEvent?.Dispose();

        base.OnExit(e);
    }
}
