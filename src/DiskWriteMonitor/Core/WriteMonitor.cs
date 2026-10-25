using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace DiskWriteMonitor.Core;

public sealed class ProcRow : INotifyPropertyChanged
{
    private ulong _bytes;
    private double _percent;
    private bool _isActive;
    private int _seriesIndex = -1;

    public ProcRow(string name) => Name = name;

    public string Name { get; }

    public ulong Bytes => _bytes;
    public string BytesText => Fmt.Bytes(_bytes);
    public double Percent => _percent;
    public bool IsActive => _isActive;
    public bool IsSelected => _seriesIndex >= 0;

    /// <summary>左侧活动圆点</summary>
    public Brush DotBrush => _isActive ? PaletteBrushes.DotActive : PaletteBrushes.DotIdle;

    /// <summary>选中时左侧的竖条与占比条用曲线色；未选中用中性色</summary>
    public Brush BarBrush => _seriesIndex >= 0 ? PaletteBrushes.Series[_seriesIndex] : PaletteBrushes.Muted;
    public Brush RowBackground => _seriesIndex >= 0 ? PaletteBrushes.SeriesBackground[_seriesIndex] : Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ulong bytes, double percent, bool active, int seriesIndex)
    {
        var changed = false;

        if (_bytes != bytes) { _bytes = bytes; changed = true; }
        if (Math.Abs(_percent - percent) > 0.0005) { _percent = percent; changed = true; }
        if (_isActive != active) { _isActive = active; changed = true; }
        if (_seriesIndex != seriesIndex) { _seriesIndex = seriesIndex; changed = true; }

        if (!changed) return;

        // 行数多，逐个属性通知会让界面做 5 次无效工作，直接整体刷新
        Raise(nameof(Bytes));
        Raise(nameof(BytesText));
        Raise(nameof(Percent));
        Raise(nameof(IsActive));
        Raise(nameof(IsSelected));
        Raise(nameof(DotBrush));
        Raise(nameof(BarBrush));
        Raise(nameof(RowBackground));
    }

    /// <summary>只换选中标记，不动数值 —— 点击后立刻高亮，不必等下一个采样周期</summary>
    public void SetSeriesIndex(int seriesIndex)
    {
        if (_seriesIndex == seriesIndex) return;
        _seriesIndex = seriesIndex;
        Raise(nameof(IsSelected));
        Raise(nameof(BarBrush));
        Raise(nameof(RowBackground));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 采样与状态机。一切数值口径以 WINDOWS-PORT.md §3 为准。
/// </summary>
public sealed class WriteMonitor : INotifyPropertyChanged
{
    private const double UnitBytes = 512_000.0;
    private const ulong ActiveThreshold = 256_000;
    private static readonly TimeSpan ActiveHold = TimeSpan.FromSeconds(2.5);
    private const int ChartPoints = 60;

    private readonly AppSettings _settings;

    // 进程差分状态
    private readonly Dictionary<int, ulong> _prevBytes = new();
    private readonly Dictionary<int, string> _prevName = new();
    private readonly Dictionary<string, ulong> _cumulative = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastActive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<double>> _procHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcRow> _rowCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _selected = new();

    /// <summary>本次会话全系统写入字节（仅在没有按盘数据时兜底）</summary>
    private double _sessionTotalBytes;

    // 每日写入流水
    private string _dailyDate = string.Empty;
    private readonly Dictionary<int, long> _dailyBytes = new();
    private double _dailySeconds;
    private int _dailyTicks;

    /// <summary>每块盘各存一份会话状态，切换磁盘只换显示，不重置任何一块的数据</summary>
    private readonly Dictionary<int, DiskSession> _sessions = new();
    private readonly DiskSession _fallbackSession = new();

    private System.Threading.Timer? _timer;
    private DiskRateCounter? _rateCounter;
    private int _busy;
    private bool _hasBaseline;
    private DateTime _prevTime = DateTime.MinValue;

    public WriteMonitor(AppSettings settings)
    {
        _settings = settings;
        Disks = SmartReader.Enumerate();
        Disk = Disks.FirstOrDefault();

        if (Disk is not null && _settings.SelectedDisk >= 0)
        {
            var saved = Disks.FirstOrDefault(d => d.Number == _settings.SelectedDisk);
            if (saved is not null) Disk = saved;
        }

        foreach (var disk in Disks) _sessions[disk.Number] = new DiskSession();

        LoadDaily();
        RebuildChips();
    }

    private void LoadDaily()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        // 上次运行跨天了：把那天残留的数据先落盘，别丢
        if (!string.IsNullOrEmpty(_settings.DailyDate) && _settings.DailyDate != today)
        {
            var carried = new Dictionary<int, long>();
            foreach (var (key, value) in _settings.DailyBytes)
                if (int.TryParse(key, out var number)) carried[number] = value;

            FlushDaily(_settings.DailyDate, carried, _settings.DailySeconds);
            _settings.DailyBytes.Clear();
            _settings.DailySeconds = 0;
        }

        _dailyDate = today;

        foreach (var (key, value) in _settings.DailyBytes)
            if (int.TryParse(key, out var number)) _dailyBytes[number] = value;

        _dailySeconds = _settings.DailySeconds;
    }

    private void FlushDaily(string date, Dictionary<int, long> bytes, double seconds)
    {
        var rows = new List<DailyWriteLog.Row>();

        foreach (var (number, value) in bytes)
        {
            if (value <= 0) continue;
            var disk = Disks.FirstOrDefault(d => d.Number == number);
            rows.Add(new DailyWriteLog.Row
            {
                Date = date,
                Disk = number,
                Model = disk?.Model ?? string.Empty,
                Letters = disk?.Letters ?? string.Empty,
                Bytes = value,
                Hours = Math.Round(seconds / 3600.0, 3)
            });
        }

        if (rows.Count > 0) DailyWriteLog.Upsert(rows);
    }

    /// <summary>累计当天写入、跨天结转、每约 5 分钟落一次盘（避免每分钟写文件）</summary>
    private void TickDaily(double dt)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (today != _dailyDate)
        {
            FlushDaily(_dailyDate, _dailyBytes, _dailySeconds);
            _dailyBytes.Clear();
            _dailySeconds = 0;
            _dailyDate = today;
        }

        _dailySeconds += dt;
        _dailyTicks++;

        if (_dailyTicks < 300) return;

        _dailyTicks = 0;
        PersistDaily();
    }

    private void PersistDaily()
    {
        if (_dailyBytes.Count == 0 && _dailySeconds < 1) return;

        _settings.DailyDate = _dailyDate;
        _settings.DailyBytes = _dailyBytes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        _settings.DailySeconds = _dailySeconds;
        _settings.Save();

        FlushDaily(_dailyDate, _dailyBytes, _dailySeconds);
    }

    private sealed class DiskSession
    {
        /// <summary>本次会话该盘写入字节（由自开机累计字节做差，精确）</summary>
        public double SessionBytes;

        /// <summary>自开机该盘累计写入字节</summary>
        public ulong BootBytes;

        public double Peak;
        public double LastRate;
        public ulong TotalBytes;
        public bool FirmwareReliable;
        public int TemperatureC = -1;
        public byte Used;
        public readonly Queue<double> History = new();

        // raw 累计字节的基线，用来算会话量与速率
        public bool HasRaw;
        public long RawFirst;
        public long RawPrev;
    }

    private DiskSession Current =>
        Disk is not null && _sessions.TryGetValue(Disk.Number, out var session) ? session : _fallbackSession;

    private void RebuildChips() =>
        DiskChips = Disks.Select(d => new DiskChip(d, d.Number == Disk?.Number)).ToList();

    /// <summary>
    /// 切换监控的磁盘。所有磁盘的会话量一直在后台累计，这里只是换显示对象，
    /// 所以来回切换不会丢数据（最初版本会清零，是错的）。
    /// </summary>
    public void SelectDisk(int number)
    {
        if (Disk is null) return;
        var target = Disks.FirstOrDefault(d => d.Number == number);
        if (target is null || target.Number == Disk.Number) return;

        Disk = target;
        _settings.SelectedDisk = target.Number;
        _settings.Save();

        StatusMessage = string.Empty;
        ShowPermissionBar = false;

        RebuildChips();
        Chart = BuildChart();

        Raise(nameof(DiskChips));
        Raise(nameof(DeviceTitle));
        Raise(nameof(DeviceSubtitle));
        Raise(nameof(DataSourceText));
        Raise(nameof(Chart));
        RaiseAll();
    }

    public List<DiskInfo> Disks { get; }
    public DiskInfo? Disk { get; private set; }

    /// <summary>磁盘选择器卡片；只有一块盘时界面不显示</summary>
    public IReadOnlyList<DiskChip> DiskChips { get; private set; } = Array.Empty<DiskChip>();

    public bool ShowDiskSelector => Disks.Count > 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start()
    {
        _rateCounter?.Dispose();
        _rateCounter = new DiskRateCounter();

        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _rateCounter?.Dispose();
        _rateCounter = null;

        // 退出时把当天流水落盘，否则今天的数据会停在内存里
        PersistDaily();
    }

    // ---------------- 采样 ----------------

    private void Tick()
    {
        if (IsPaused) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            DiskHealth? health = null;
            if (Disk is not null) health = SmartReader.Read(Disk);

            // 一次采集覆盖所有物理磁盘；raw 是自开机累计字节（精确），rates 仅作兜底
            var raws = new Dictionary<int, long>();
            var rates = new Dictionary<int, double>();
            var rateOk = _rateCounter is not null && _rateCounter.TryReadAll(out raws, out rates);

            var procs = ProcessSampler.Snapshot(out var total, out var failed);
            var now = DateTime.Now;

            Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => Apply(health, rateOk, raws, rates, procs, total, failed, now)));
        }
        catch
        {
            // 采样异常不该让应用退出
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Apply(DiskHealth? health, bool rateOk, Dictionary<int, long> raws,
        Dictionary<int, double> rates, Dictionary<int, ProcWrite> procs, int total, int failed, DateTime now)
    {
        var dt = _hasBaseline ? Math.Clamp((now - _prevTime).TotalSeconds, 0.2, 10.0) : 1.0;
        if (dt <= 0) dt = 1.0;

        ApplyDisk(health, rateOk, raws, rates, now, dt);
        ApplyProcesses(procs, now, dt, total, failed);

        LastUpdateText = "更新于 " + Fmt.Time(now);
        Chart = BuildChart();
        RaiseAll();
    }

    private void ApplyDisk(DiskHealth? health, bool rateOk, Dictionary<int, long> raws,
        Dictionary<int, double> rates, DateTime now, double dt)
    {
        _prevTime = now;
        _hasBaseline = true;
        TickDaily(dt);

        if (rateOk && raws.Count > 0)
        {
            // 精确路径：自开机累计字节做差，同时得到会话量与速率
            foreach (var (number, cumulative) in raws)
            {
                if (!_sessions.TryGetValue(number, out var each)) continue;

                each.BootBytes = (ulong)cumulative;

                if (!each.HasRaw)
                {
                    each.HasRaw = true;
                    each.RawFirst = cumulative;
                    each.RawPrev = cumulative;
                    continue;
                }

                var delta = cumulative >= each.RawPrev ? cumulative - each.RawPrev : 0;
                each.RawPrev = cumulative;
                each.SessionBytes = cumulative >= each.RawFirst ? cumulative - each.RawFirst : 0;
                each.LastRate = delta / 1e6 / dt;
                if (each.LastRate > each.Peak) each.Peak = each.LastRate;

                // 每日流水：只累加真实落盘增量
                if (delta > 0)
                    _dailyBytes[number] = (_dailyBytes.TryGetValue(number, out var dayAcc) ? dayAcc : 0) + delta;
            }

            PushHistory(Current, Current.LastRate);

            StatusLevel = 0;
            StatusText = "实时";
        }
        else if (rateOk && rates.Count > 0)
        {
            // 兜底路径：raw 读失败时退回 PDH 的速率，会话量靠积分（精度差，界面会标注）
            foreach (var (number, bytesPerSecond) in rates)
            {
                if (!_sessions.TryGetValue(number, out var each)) continue;

                each.LastRate = bytesPerSecond / 1e6;
                each.SessionBytes += bytesPerSecond * dt;
                if (each.LastRate > each.Peak) each.Peak = each.LastRate;
            }

            PushHistory(Current, Current.LastRate);

            StatusLevel = 0;
            StatusText = "实时";
        }
        else
        {
            Current.LastRate = 0;
            PushHistory(Current, 0);

            StatusLevel = 2;
            StatusText = "读取失败";
            StatusMessage = "读不到磁盘写入速率（性能计数器不可用）";
            ShowPermissionBar = false;
            return;
        }

        if (health is null)
        {
            StatusLevel = 2;
            StatusText = "没有可用磁盘";
            StatusMessage = "没有找到可监控的物理磁盘";
            ShowPermissionBar = false;
            return;
        }

        var session = Current;

        switch (health.Status)
        {
            case DiskHealthStatus.Ok:
                session.TemperatureC = health.TemperatureC;
                session.Used = health.PercentageUsed;
                session.FirmwareReliable = health.FirmwareReliable;
                session.TotalBytes = health.FirmwareReliable ? health.TotalBytes : 0;
                StatusMessage = health.FirmwareReliable ? string.Empty : health.Message;
                ShowPermissionBar = false;
                break;

            case DiskHealthStatus.NoPermission:
                StatusMessage = "累计写入、温度、寿命需要管理员权限";
                ShowPermissionBar = true;
                break;

            case DiskHealthStatus.NotSupported:
                StatusMessage = string.IsNullOrEmpty(health.Message) ? "该硬盘不支持写入寿命统计" : health.Message;
                ShowPermissionBar = false;
                break;

            default:
                StatusMessage = health.Message;
                ShowPermissionBar = false;
                break;
        }
    }

    private static void PushHistory(DiskSession session, double rate)
    {
        session.History.Enqueue(rate);
        while (session.History.Count > ChartPoints) session.History.Dequeue();
    }

    private void ApplyProcesses(Dictionary<int, ProcWrite> procs, DateTime now, double dt, int total, int failed)
    {
        var deltas = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();

        foreach (var (pid, w) in procs)
        {
            seen.Add(pid);
            ulong delta = 0;

            // 三个条件同时满足才算同一条命：有旧基线、计数没退、进程名没变
            // 进程名变了说明 PID 被别的程序复用，必须重新取基准
            if (_prevBytes.TryGetValue(pid, out var last)
                && w.Bytes >= last
                && _prevName.TryGetValue(pid, out var prevName)
                && string.Equals(prevName, w.Name, StringComparison.OrdinalIgnoreCase))
            {
                delta = w.Bytes - last;
            }

            _prevBytes[pid] = w.Bytes;
            _prevName[pid] = w.Name;

            if (!_cumulative.ContainsKey(w.Name)) _cumulative[w.Name] = 0;
            if (delta > 0)
            {
                _cumulative[w.Name] += delta;

                // 会话总量直接来自进程计数器的精确增量，不做任何积分
                _sessionTotalBytes += delta;
            }

            if (delta > 0) deltas[w.Name] = deltas.TryGetValue(w.Name, out var d) ? d + delta : delta;
            if (delta >= ActiveThreshold) _lastActive[w.Name] = now;
        }

        // 已退出的进程：清基线和名字，但累计值按契约保留
        if (_prevBytes.Count > seen.Count)
        {
            foreach (var pid in _prevBytes.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _prevBytes.Remove(pid);
                _prevName.Remove(pid);
            }
        }

        // 曲线补 0，保证每个名字每秒都有一个点，不会出现断层
        foreach (var name in _procHistory.Keys.ToList())
        {
            if (!deltas.ContainsKey(name)) PushProc(name, 0);
        }

        foreach (var (name, d) in deltas)
        {
            if (d > 0 || _procHistory.ContainsKey(name)) PushProc(name, d / 1e6 / dt);
        }

        Trim();

        ProcessCountText = failed > 0
            ? $"{_cumulative.Count} 个进程（{failed} 个读不到计数）"
            : $"{_cumulative.Count} 个进程";

        BuildRows(now);
    }

    private void BuildRows(DateTime now)
    {
        var max = 1UL;
        foreach (var v in _cumulative.Values) if (v > max) max = v;

        var cutoff = now - ActiveHold;
        var list = new List<ProcRow>(_cumulative.Count);

        foreach (var (name, bytes) in _cumulative)
        {
            if (!_rowCache.TryGetValue(name, out var row))
            {
                row = new ProcRow(name);
                _rowCache[name] = row;
            }

            var active = _lastActive.TryGetValue(name, out var t) && t >= cutoff;
            var index = _selected.IndexOf(name);
            row.Apply(bytes, (double)bytes / max, active, index);
            list.Add(row);
        }

        list.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        Rows = list;
    }

    private void Trim()
    {
        if (_cumulative.Count <= 1200) return;

        var keep = _cumulative.OrderByDescending(kv => kv.Value)
                              .Take(1000)
                              .Select(kv => kv.Key)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _cumulative.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cumulative.Remove(key);
            _procHistory.Remove(key);
            _rowCache.Remove(key);
            _lastActive.Remove(key);
            _selected.Remove(key);
        }
    }

    private void PushProc(string name, double rate)
    {
        if (!_procHistory.TryGetValue(name, out var q))
        {
            q = new Queue<double>();
            _procHistory[name] = q;
        }

        q.Enqueue(rate);
        while (q.Count > ChartPoints) q.Dequeue();
    }

    private ChartData BuildChart()
    {
        if (_selected.Count == 0)
        {
            return new ChartData
            {
                IsDisk = true,
                Series = new List<ChartSeries>
                {
                    new()
                    {
                        Name = "整盘",
                        ColorHex = Palette.Accent,
                        Points = Current.History.ToArray()
                    }
                }
            };
        }

        var list = new List<ChartSeries>(_selected.Count);
        for (var i = 0; i < _selected.Count; i++)
        {
            var name = _selected[i];
            var points = _procHistory.TryGetValue(name, out var q) ? q.ToArray() : Array.Empty<double>();
            list.Add(new ChartSeries
            {
                Name = name,
                ColorHex = Palette.Series[i],
                Points = points
            });
        }

        return new ChartData { IsDisk = false, Series = list };
    }

    // ---------------- 交互 ----------------

    public bool IsPaused { get; private set; }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
        {
            // 恢复时重取基准，否则暂停期间的增量会算成一次爆发
            _hasBaseline = false;
        }
        Raise(nameof(IsPaused));
        Raise(nameof(PauseText));
    }

    public string PauseText => IsPaused ? "继续" : "暂停";

    public bool ToggleSelection(string name)
    {
        var index = _selected.IndexOf(name);
        if (index >= 0)
        {
            _selected.RemoveAt(index);
        }
        else
        {
            if (_selected.Count >= Palette.MaxSelection) return false; // 第 7 个点击直接忽略
            _selected.Add(name);
        }

        Chart = BuildChart();
        RefreshSelectionVisuals();
        Raise(nameof(Chart));
        Raise(nameof(ChartTitle));
        Raise(nameof(SelectionHint));
        return true;
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Chart = BuildChart();
        RefreshSelectionVisuals();
        Raise(nameof(Chart));
        Raise(nameof(ChartTitle));
        Raise(nameof(SelectionHint));
    }

    /// <summary>
    /// 选中顺序决定曲线颜色，所以任何一个增删都会让后面所有行的颜色变，
    /// 这里整批重算一次，避免"要等下一帧才变色"的滞后感。
    /// </summary>
    private void RefreshSelectionVisuals()
    {
        foreach (var (name, row) in _rowCache)
            row.SetSeriesIndex(_selected.IndexOf(name));
    }

    public string ChartTitle => _selected.Count == 0
        ? "整盘写入 · 60 秒"
        : $"已选 {_selected.Count} 个进程 · 60 秒";

    /// <summary>进程排行的口径名（当前固定为本次会话）</summary>
    public string ProcessSectionTitle => "本次会话 · 谁在写";

    public string SelectionHint => _selected.Count == 0 ? "点击进程可叠加曲线" : "点「清除」回到整盘";

    // ---------------- 对外属性 ----------------

    public IReadOnlyList<ProcRow> Rows { get; private set; } = Array.Empty<ProcRow>();
    public ChartData Chart { get; private set; } = ChartData.Empty;

    public string DeviceTitle => Disk?.Title ?? "未检测到磁盘";
    public string DeviceSubtitle => Disk?.Identifier ?? "—";

    private int _statusLevel;
    public int StatusLevel { get => _statusLevel; private set => _statusLevel = value; }
    public string StatusText { get; private set; } = "初始化";
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>提示条标题：说清楚缺的是什么，而不是笼统说"读不到"</summary>
    public string StatusTitle => ShowPermissionBar ? "累计写入需要管理员权限"
        : StatusLevel != 0 ? "读不到磁盘写入数据"
        : "部分数据不可用";

    /// <summary>有缺失就显示提示条（没权限、盘不支持、读取失败都算）</summary>
    public bool ShowStatusBar => StatusLevel != 0 || !string.IsNullOrEmpty(StatusMessage);

    /// <summary>只有"没权限"才值得给提权按钮，其它失败提权也没用</summary>
    public bool ShowPermissionBar { get; private set; }

    // 以下数值全部取自"当前磁盘"的会话状态，切换磁盘时自动变成另一块盘的数字
    public double CurrentRate => Current.LastRate;
    public string RateValue => Fmt.Rate(CurrentRate).Value;
    public string RateUnit => Fmt.Rate(CurrentRate).Unit;

    /// <summary>当前选中磁盘的本次会话写入量（按盘统计，精确）</summary>
    public double SessionBytes => Current.HasRaw ? Current.SessionBytes : _sessionTotalBytes;
    public string SessionText => Fmt.Bytes(SessionBytes);
    public string SessionCaption => Current.HasRaw ? "当前磁盘" : "全系统合计";

    /// <summary>
    /// 固件累计写入（出厂至今）。固件值不可信时（如廉价 SATA 盘）退回"自开机累计"，
    /// 至少还有一个可用的量级参考，而不是显示一串破折号。
    /// </summary>
    public double TotalBytes => Current.FirmwareReliable ? Current.TotalBytes : Current.BootBytes;
    public string TotalText => TotalBytes > 0 ? Fmt.Bytes(TotalBytes) : "—";

    public string TotalCaption => Current.FirmwareReliable
        ? (PercentageUsed > 0 ? $"出厂至今 · 已用寿命 {PercentageUsed}%" : "出厂至今")
        : "自开机累计";

    public string PeakText => Fmt.RateText(Current.Peak);

    public int TemperatureC => Current.TemperatureC;
    public string TempText => TemperatureC >= 0 ? TemperatureC + " ℃" : "—";

    public byte PercentageUsed => Current.Used;
    public string UsedText => PercentageUsed > 0 ? PercentageUsed + " %" : "—";

    /// <summary>调试用：每块盘的自开机累计 / 会话量 / 速率，便于和系统计数器对账</summary>
    public string DiskDebugText => string.Join(" | ", _sessions
        .OrderBy(kv => kv.Key)
        .Select(kv => $"d{kv.Key} boot={kv.Value.BootBytes} sess={(long)kv.Value.SessionBytes} rate={kv.Value.LastRate:F3}"))
        + $" || daily={_dailyDate}/{_dailySeconds:F1}s bytes={string.Join(",", _dailyBytes.Select(kv => kv.Key + ":" + kv.Value))}";

    public string ProcessCountText { get; private set; } = "0 个进程";

    /// <summary>调试用：把当前磁盘最近 60 秒的速率序列打出来，便于核对积分准确度</summary>
    public string RateHistoryText => string.Join(",", Current.History.Select(v => v.ToString("F2")));
    public string LastUpdateText { get; private set; } = "—";
    public string DataSourceText => Disk is null || Disk.BusType == 0
        ? "数据来源：硬盘固件健康日志 · 1 秒采样"
        : $"数据来源：{Disk.BusName} 健康日志 · 1 秒采样";

    private void RaiseAll()
    {
        Raise(nameof(Rows));
        Raise(nameof(Chart));
        Raise(nameof(ChartTitle));
        Raise(nameof(SelectionHint));
        Raise(nameof(CurrentRate));
        Raise(nameof(RateValue));
        Raise(nameof(RateUnit));
        Raise(nameof(SessionBytes));
        Raise(nameof(SessionText));
        Raise(nameof(SessionCaption));
        Raise(nameof(TotalBytes));
        Raise(nameof(TotalText));
        Raise(nameof(TotalCaption));
        Raise(nameof(PeakText));
        Raise(nameof(TemperatureC));
        Raise(nameof(TempText));
        Raise(nameof(PercentageUsed));
        Raise(nameof(UsedText));
        Raise(nameof(ProcessCountText));
        Raise(nameof(ProcessSectionTitle));
        Raise(nameof(LastUpdateText));
        Raise(nameof(StatusLevel));
        Raise(nameof(StatusText));
        Raise(nameof(StatusMessage));
        Raise(nameof(StatusTitle));
        Raise(nameof(ShowStatusBar));
        Raise(nameof(ShowPermissionBar));
        Raise(nameof(DeviceTitle));
        Raise(nameof(DeviceSubtitle));
        Raise(nameof(DiskChips));
        Raise(nameof(ShowDiskSelector));
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
