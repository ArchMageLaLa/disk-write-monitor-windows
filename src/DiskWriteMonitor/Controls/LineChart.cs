using System.Windows;
using System.Windows.Media;

namespace DiskWriteMonitor.Controls;

/// <summary>
/// 60 秒曲线。手绘而非引第三方图表库，理由：只有一种图、要精确控制配色与留白、不想为它背依赖。
/// 对应 macOS 版 SwiftUI Canvas 的那段绘制。
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Core.ChartData), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Core.ChartData? Data
    {
        get => (Core.ChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        const double left = 44;
        const double top = 12;
        const double rightPad = 4;
        const double bottomPad = 18;

        var right = w - rightPad;
        var bottom = h - bottomPad;
        if (right <= left || bottom <= top) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 背景卡片
        var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x20));
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        var data = Data;
        var series = data?.Series ?? new List<Core.ChartSeries>();
        var max = ComputeMax(series);
        if (max <= 0) max = 1;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        gridPen.Freeze();
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
        labelBrush.Freeze();

        // 横向网格 + 左侧刻度
        const int lines = 3;
        for (var i = 0; i <= lines; i++)
        {
            var ratio = (double)i / lines;
            var y = bottom - ratio * (bottom - top);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));

            var text = new FormattedText(FormatTick(max * ratio), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, labelBrush, dpi);
            dc.DrawText(text, new Point(left - 8 - text.Width, y - text.Height / 2));
        }

        if (series.Count == 0) return;

        const int slots = 60;
        var step = (right - left) / (slots - 1);

        foreach (var s in series)
        {
            var points = s.Points;
            if (points.Length == 0) continue;

            var color = (Color)ColorConverter.ConvertFromString(s.ColorHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, data?.IsDisk == true ? 1.6 : 1.3);
            pen.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var started = false;
                for (var i = 0; i < points.Length; i++)
                {
                    // 右端对齐"现在"，点数不足 60 时曲线从右侧往左长出来
                    var x = right - (points.Length - 1 - i) * step;
                    var y = bottom - Math.Clamp(points[i] / max, 0, 1) * (bottom - top);

                    if (!started)
                    {
                        ctx.BeginFigure(new Point(x, y), false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                }
            }
            geo.Freeze();

            // 单条（整盘）画面积渐变，多条只画线，避免互相遮挡
            if (data?.IsDisk == true && points.Length > 1)
            {
                var fill = new LinearGradientBrush(
                    Color.FromArgb(0x44, color.R, color.G, color.B),
                    Color.FromArgb(0x00, color.R, color.G, color.B),
                    new Point(0, 0), new Point(0, 1));
                fill.Freeze();

                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    var started = false;
                    for (var i = 0; i < points.Length; i++)
                    {
                        var x = right - (points.Length - 1 - i) * step;
                        var y = bottom - Math.Clamp(points[i] / max, 0, 1) * (bottom - top);
                        if (!started) { ctx.BeginFigure(new Point(x, y), true, false); started = true; }
                        else ctx.LineTo(new Point(x, y), true, false);
                    }
                    var lastX = right;
                    ctx.LineTo(new Point(lastX, bottom), true, false);
                    ctx.LineTo(new Point(right - (points.Length - 1) * step, bottom), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            dc.DrawGeometry(null, pen, geo);
        }
    }

    private static double ComputeMax(List<Core.ChartSeries> series)
    {
        var max = 0.0;
        foreach (var s in series)
            foreach (var v in s.Points)
                if (v > max) max = v;
        return NiceMax(max);
    }

    /// <summary>把最大值抬到 1 / 2 / 5 x 10^n，刻度才不会出现 3.7 这种数。</summary>
    public static double NiceMax(double value)
    {
        if (value <= 0) return 1;
        var exp = Math.Floor(Math.Log10(value));
        var scale = Math.Pow(10, exp);
        var norm = value / scale;

        var nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return nice * scale;
    }

    private static string FormatTick(double v)
    {
        if (v >= 100) return v.ToString("F0");
        if (v >= 10) return v.ToString("F0");
        if (v >= 1) return v.ToString("F1");
        return v.ToString("F2");
    }
}
