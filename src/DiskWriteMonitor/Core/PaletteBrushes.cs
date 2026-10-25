using System.Windows.Media;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 冻结的画笔。每秒刷新 1000 行时如果每次 new Brush 会拖慢界面，
/// 这里一次性建好并 Freeze，之后只做引用赋值。
/// </summary>
public static class PaletteBrushes
{
    public static readonly SolidColorBrush[] Series = CreateSeries();
    public static readonly SolidColorBrush[] SeriesBackground = CreateSeriesBackground();
    public static readonly SolidColorBrush DotActive = Frozen(Palette.Ok);
    public static readonly SolidColorBrush DotIdle = Frozen("#1FFFFFFF");
    public static readonly SolidColorBrush Text = Frozen(Palette.Text);
    public static readonly SolidColorBrush Muted = Frozen(Palette.Muted);
    public static readonly SolidColorBrush Accent = Frozen(Palette.Accent);
    public static readonly SolidColorBrush Ok = Frozen(Palette.Ok);
    public static readonly SolidColorBrush Warn = Frozen(Palette.Warn);
    public static readonly SolidColorBrush Bad = Frozen(Palette.Bad);
    public static readonly SolidColorBrush Background = Frozen(Palette.Background);
    public static readonly SolidColorBrush Surface = Frozen(Palette.Surface);

    private static SolidColorBrush[] CreateSeries()
    {
        var list = new SolidColorBrush[Palette.Series.Length];
        for (var i = 0; i < Palette.Series.Length; i++) list[i] = Frozen(Palette.Series[i]);
        return list;
    }

    private static SolidColorBrush[] CreateSeriesBackground()
    {
        var list = new SolidColorBrush[Palette.Series.Length];
        for (var i = 0; i < Palette.Series.Length; i++)
        {
            var c = (Color)ColorConverter.ConvertFromString(Palette.Series[i]);
            list[i] = Frozen(Color.FromArgb(0x1A, c.R, c.G, c.B)); // 10% 透明度
        }
        return list;
    }

    private static SolidColorBrush Frozen(string hex) =>
        Frozen((Color)ColorConverter.ConvertFromString(hex));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
