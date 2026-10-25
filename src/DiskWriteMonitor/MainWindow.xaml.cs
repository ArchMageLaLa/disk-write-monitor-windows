using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DiskWriteMonitor.Core;

namespace DiskWriteMonitor;

public partial class MainWindow : Window
{
    private const double BaseHeight = 794;
    private const double BannerHeight = 48;
    private const double PermissionHeight = 58;
    private const double SelectorHeight = 44;

    private readonly WriteMonitor _monitor;
    private readonly UpdateChecker _updater;
    private bool _forceClose;
    private bool _closeHintShown;

    public MainWindow(WriteMonitor monitor, UpdateChecker updater)
    {
        InitializeComponent();

        _monitor = monitor;
        _updater = updater;
        DataContext = monitor;

        monitor.PropertyChanged += Monitor_PropertyChanged;
        updater.Changed += Updater_Changed;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        RefreshBars();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;

        // 关闭窗口 ≠ 退出应用，收进托盘继续采样
        e.Cancel = true;
        App.Current.HideToTray(!_closeHintShown);
        _closeHintShown = true;
    }

    private void Monitor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WriteMonitor.ShowStatusBar) or nameof(WriteMonitor.StatusMessage))
            RefreshBars();
    }

    private void Updater_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshBars));
            return;
        }
        RefreshBars();
    }

    private void RefreshBars()
    {
        var showBar = _monitor.ShowStatusBar;
        PermissionBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        PermissionText.Text = string.IsNullOrEmpty(_monitor.StatusMessage)
            ? "读不到这块硬盘的写入数据。"
            : _monitor.StatusMessage;

        var release = _updater.Available;
        var showUpdate = release is not null && _updater.ShouldShowBanner;
        UpdateBanner.Visibility = showUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (release is not null) UpdateText.Text = "发现新版本 v" + release.Version;

        Height = BaseHeight
                 + (_monitor.ShowDiskSelector ? SelectorHeight : 0)
                 + (showBar ? PermissionHeight : 0)
                 + (showUpdate ? BannerHeight : 0);
    }

    private void DiskChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var number = element.Tag switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => -1
            };

            if (number >= 0) _monitor.SelectDisk(number);
        }

        e.Handled = true;
    }

    // ---------------- 交互 ----------------

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Pause_Click(object sender, RoutedEventArgs e) => _monitor.TogglePause();

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _monitor.ClearSelection();

    private void Row_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcRow row })
        {
            _monitor.ToggleSelection(row.Name);
            e.Handled = true;
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var want = AutoStartCheck.IsChecked == true;
        if (want == AutoStart.IsEnabled()) return; // 初始化时不要白写一次注册表
        AutoStart.Set(want);
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e) => App.Current.RestartElevated();

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        var url = _updater.Available?.Url;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 没默认浏览器就算了 */ }
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => _updater.Dismiss();
}
