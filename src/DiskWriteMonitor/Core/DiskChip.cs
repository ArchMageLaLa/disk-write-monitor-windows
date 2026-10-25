using System.Windows.Media;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 磁盘选择器上的一枚卡片。每次切换磁盘会整批重建，所以不做属性通知。
/// </summary>
public sealed class DiskChip
{
    private static readonly SolidColorBrush SelectedBg = Freeze(0x1F, 0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush NormalBg = Freeze(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush NormalBorder = Freeze(0x1A, 0xFF, 0xFF, 0xFF);

    public DiskChip(DiskInfo disk, bool selected)
    {
        Disk = disk;
        IsSelected = selected;
    }

    public DiskInfo Disk { get; }
    public bool IsSelected { get; }

    public string Label => "磁盘 " + Disk.Number;

    public string Detail
    {
        get
        {
            if (!string.IsNullOrEmpty(Disk.Letters)) return Disk.Letters;
            return Disk.BusName;
        }
    }

    public Brush Background => IsSelected ? SelectedBg : NormalBg;
    public Brush BorderBrush => IsSelected ? PaletteBrushes.Accent : NormalBorder;
    public Brush TextBrush => IsSelected ? PaletteBrushes.Text : PaletteBrushes.Muted;

    private static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
