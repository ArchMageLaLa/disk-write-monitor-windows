using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DiskWriteMonitor.Core;

/// <summary>0 正常 / 1 警告 / 2 错误 → 文字色</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return level switch
        {
            1 => PaletteBrushes.Warn,
            2 => PaletteBrushes.Bad,
            _ => PaletteBrushes.Ok
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0 / 1 / 2 → 胶囊底色（同色 18%）</summary>
public sealed class StatusBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        var hex = level switch
        {
            1 => Palette.Warn,
            2 => Palette.Bad,
            _ => Palette.Ok
        };
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 0~1 的比例 → 像素宽度。
/// 不能把 ScaleTransform 绑到 Percent 上：RenderTransform 是 Freezable，不在可视树里，
/// 拿不到 DataContext，绑定会静默失效（第一版就是这么踩的）。
/// </summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        var max = 96.0;

        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            max = parsed;

        var clamped = Math.Clamp(percent, 0, 1);
        if (clamped <= 0) return 0.0;

        // 有写入但占比极小时至少给 2px，否则用户会以为这行没数据
        return Math.Max(2.0, clamped * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
