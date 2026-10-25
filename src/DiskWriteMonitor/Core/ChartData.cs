namespace DiskWriteMonitor.Core;

/// <summary>配色，严格对应 WINDOWS-PORT.md §4。</summary>
public static class Palette
{
    public const string Background = "#0B0D10";
    public const string Surface = "#141A20";
    public const string Text = "#E9EDF2";
    public const string Muted = "#8A94A6";
    public const string Accent = "#38BDF8";
    public const string Ok = "#34D399";
    public const string Warn = "#FBBF24";
    public const string Bad = "#F87171";

    /// <summary>曲线颜色，按选中顺序固定分配，最多 6 条。</summary>
    public static readonly string[] Series =
    {
        "#38BDF8", "#5AD896", "#FBA54D", "#C994F8", "#F8718B", "#F0D65A"
    };

    public const int MaxSelection = 6;
}

public sealed class ChartSeries
{
    public string Name { get; init; } = string.Empty;
    public string ColorHex { get; init; } = Palette.Accent;
    public double[] Points { get; init; } = Array.Empty<double>();
}

public sealed class ChartData
{
    public List<ChartSeries> Series { get; init; } = new();
    public double YMax { get; init; } = 1;
    public bool IsDisk { get; init; } = true;

    public static readonly ChartData Empty = new();
}
