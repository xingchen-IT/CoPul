using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;

namespace CoPul.App.Sdk;

/// <summary>硬盘区块的一行。型号 / 类型 / 容量来自 <see cref="SystemInfo"/>，温度每次快照刷新。</summary>
public sealed class DiskRow : INotifyPropertyChanged
{
    private string _temp = Fmt.Dash;
    private Level _level = Level.Muted;

    public DiskRow(int index, string model, string bus, double capacityGb)
    {
        Index = index;
        Model = model;
        Bus = bus;
        CapacityText = $"{capacityGb:F0} GB";
    }

    /// <summary>与 <c>DiskTemperature</c> 数组下标 / <c>PhysicalDriveN</c> 的 N 一致。</summary>
    public int Index { get; }
    public string Model { get; }
    public string Bus { get; }
    public string CapacityText { get; }

    public string Temp => _temp;

    public Brush TempBrush => Palette.Brush(_level switch
    {
        Level.Warn => "BrushWarn",
        Level.Danger => "BrushDanger",
        Level.Muted => "BrushTextSecondary",
        _ => "AccentDisk"
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetTemp(double celsius)
    {
        string text = $"{celsius:F0}°C";
        var level = Severity.Temp(celsius);
        if (_temp == text && _level == level) return;

        _temp = text;
        _level = level;
        Raise(nameof(Temp));
        Raise(nameof(TempBrush));
    }

    public void ClearTemp()
    {
        if (_temp == Fmt.Dash && _level == Level.Muted) return;
        _temp = Fmt.Dash;
        _level = Level.Muted;
        Raise(nameof(Temp));
        Raise(nameof(TempBrush));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>阈值判定。数字来自需求 10.3，改这里等于改全局配色行为，别散落到各处。</summary>
public static class Severity
{
    public static Level Temp(double celsius) => celsius > 80 ? Level.Danger
        : celsius > 65 ? Level.Warn
        : Level.Normal;

    public static Level Load(double percent) => percent > 90 ? Level.Danger
        : percent > 70 ? Level.Warn
        : Level.Normal;
}

/// <summary>
/// 大窗口的展示状态。**只做「快照 → 文案」的翻译**，不含任何采集与线程逻辑；
/// <see cref="ApplySnapshot"/> 由 CollectorHost 保证在 UI 线程调用。
/// </summary>
public sealed class DashboardState : INotifyPropertyChanged
{
    private string _cpuModel = "读取中…";
    private string _gpuModel = "读取中…";
    private string _coresText = "";
    private string _loadAvgText = Fmt.Dash;
    private string _memHeadline = "读取中…";
    private string _memDetail = "";
    private string _diskSummary = "";
    private string _partitionText = "";
    private string _gpuExtraText = "";
    private string _statusText = "正在初始化采集器…";

    public DashboardState()
    {
        CpuLoad = new MetricCell("占用率", "%", "AccentCpu");
        CpuTemp = new MetricCell("封装温度", "°C", "AccentCpu");
        CpuClock = new MetricCell("核心频率", "GHz", "AccentCpu");
        CpuPower = new MetricCell("封装功耗", "W", "AccentPower");

        GpuLoad = new MetricCell("占用率", "%", "AccentGpu");
        GpuTemp = new MetricCell("核心温度", "°C", "AccentGpu");
        GpuPower = new MetricCell("板卡功耗", "W", "AccentPower");
        GpuMem = new MetricCell("显存", "GB", "AccentVram");
        GpuClock = new MetricCell("核心频率", "MHz", "AccentGpu");
        GpuFan = new MetricCell("风扇", "%", "AccentGpu");

        MemLoad = new MetricCell("占用率", "%", "AccentRam");
        MemUsed = new MetricCell("已用", "GB", "AccentRam");
        MemCommit = new MetricCell("提交", "GB", "AccentRam");
        MemCached = new MetricCell("已缓存", "GB", "AccentRam");
    }

    // ---- 数值格 ----
    public MetricCell CpuLoad { get; }
    public MetricCell CpuTemp { get; }
    public MetricCell CpuClock { get; }
    public MetricCell CpuPower { get; }

    public MetricCell GpuLoad { get; }
    public MetricCell GpuTemp { get; }
    public MetricCell GpuPower { get; }
    public MetricCell GpuMem { get; }
    public MetricCell GpuClock { get; }
    public MetricCell GpuFan { get; }

    public MetricCell MemLoad { get; }
    public MetricCell MemUsed { get; }
    public MetricCell MemCommit { get; }
    public MetricCell MemCached { get; }

    public ObservableCollection<DiskRow> Disks { get; } = new();

    // ---- 文案 ----
    public string CpuModel { get => _cpuModel; private set => Set(ref _cpuModel, value); }
    public string GpuModel { get => _gpuModel; private set => Set(ref _gpuModel, value); }
    public string CoresText { get => _coresText; private set => Set(ref _coresText, value); }
    public string LoadAvgText { get => _loadAvgText; private set => Set(ref _loadAvgText, value); }
    public string MemHeadline { get => _memHeadline; private set => Set(ref _memHeadline, value); }
    public string MemDetail { get => _memDetail; private set => Set(ref _memDetail, value); }
    public string DiskSummary { get => _diskSummary; private set => Set(ref _diskSummary, value); }
    public string PartitionText { get => _partitionText; private set => Set(ref _partitionText, value); }
    public string GpuExtraText { get => _gpuExtraText; private set => Set(ref _gpuExtraText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public Brush StatusBrush => Palette.Brush(_statusWarn ? "BrushWarn" : "BrushTextTertiary");

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _infoNote = "";
    private bool _statusWarn;

    /// <summary>临时提示（比如点了尚未实现的设置入口）。不弹对话框，避免打断监控。</summary>
    public void ShowHint(string text) => StatusText = text;

    /// <summary>
    /// 把 Provider 的降级原因摊到状态栏。4.2 口径说明要求「不得静默降级」——
    /// 没提权时 CPU 温度/硬盘温度一律是 `—`，不写清原因用户只会以为软件坏了。
    /// </summary>
    public void ApplyProviders(IReadOnlyList<IMetricProvider> providers)
    {
        var degraded = providers
            .Where(p => p.Status != ProviderStatus.Ready)
            .Select(p => $"{p.Name}：{(p.UnavailableReason ?? "不可用").TrimEnd('。', '；', '，')}")
            .ToList();

        _statusWarn = degraded.Count > 0;
        StatusText = JoinNotes(_infoNote, degraded.Count > 0 ? "降级 " + string.Join("；", degraded) : "");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    private static string JoinNotes(params string[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? "采集正常" : string.Join(" · ", list);
    }

    /// <summary>静态信息到货：型号、盘列表、分区。M1 只在启动读一次（D04 的 10s 刷新留待 M1.2）。</summary>
    public void ApplyInfo(SystemInfo info)
    {
        CpuModel = info.CpuName.Length > 0 ? info.CpuName : "未知 CPU";

        GpuModel = info.GpuName.Length > 0
            ? info.GpuName
            : "未知 GPU";

        Disks.Clear();
        foreach (var d in info.Disks)
            Disks.Add(new DiskRow(d.Index, d.Model, d.Bus, d.CapacityGb));

        PartitionText = info.Partitions.Count == 0
            ? "分区信息不可用"
            : string.Join("    ", info.Partitions.Select(p =>
                $"{p.Name}  {p.UsedPercent:F0}%  ({p.TotalMb / 1024 - p.FreeMb / 1024:F0}/{p.TotalMb / 1024:F0} GB)"));

        StatusText = info.Warnings.Count == 0 ? "采集正常" : string.Join(" · ", info.Warnings);
        _infoNote = info.Warnings.Count == 0 ? "" : string.Join(" · ", info.Warnings);
    }

    /// <summary>自测用：跳过「各核心」长文本的更新，用来判断布局成本是否集中在它上面。</summary>
    public bool SuppressCoreLine { get; set; }

    /// <summary>每帧刷新（0.5s 一次），只做字符串格式化与赋值。</summary>
    public void ApplySnapshot(MetricSnapshot snap)
    {
        // ---------------- CPU ----------------
        SetCell(CpuLoad, snap, MetricId.CpuTotalLoad, Fmt.Pct, Severity.Load, withBar: true);
        SetCell(CpuTemp, snap, MetricId.CpuPackageTemp, Fmt.Temp, Severity.Temp);
        SetCell(CpuClock, snap, MetricId.CpuClockGhz, Fmt.Ghz);
        SetCell(CpuPower, snap, MetricId.CpuPackagePowerW, Fmt.Watts);

        if (SuppressCoreLine)
        {
            // 自测分支：什么都不写
        }
        else if (snap.TryGetSeries(MetricId.CpuCoreLoad, out var cores) && cores.IsUsable && cores.Values.Length > 0)
            CoresText = string.Join(" ", cores.Values.Select(v =>
                double.IsNaN(v) ? "  —" : v.ToString("F0").PadLeft(3)));
        else
            CoresText = "各核心占用率不可用";

        LoadAvgText = FormatLoadAvg(snap);

        // ---------------- GPU ----------------
        SetCell(GpuLoad, snap, MetricId.GpuLoad, Fmt.Pct, Severity.Load, withBar: true);
        SetCell(GpuTemp, snap, MetricId.GpuTemp, Fmt.Temp, Severity.Temp);
        SetCell(GpuPower, snap, MetricId.GpuPowerW, Fmt.Watts);
        SetCell(GpuClock, snap, MetricId.GpuClockCoreMhz, Fmt.Mhz);
        SetCell(GpuFan, snap, MetricId.GpuFanPct, Fmt.Pct);

        double? memUsed = Value(snap, MetricId.GpuMemUsedMb);
        double? memTotal = Value(snap, MetricId.GpuMemTotalMb);
        double? memPct = Value(snap, MetricId.GpuMemLoad);
        if (memUsed.HasValue && memTotal.HasValue)
            GpuMem.Apply($"{Fmt.Gb(memUsed.Value)} / {Fmt.Gb(memTotal.Value)}",
                         Severity.Load(memPct ?? 0), bar: memPct);
        else
            GpuMem.Unavailable();

        string enc = TryText(snap, MetricId.GpuEncoderLoad, v => v.ToString("F0"));
        string dec = TryText(snap, MetricId.GpuDecoderLoad, v => v.ToString("F0"));
        GpuExtraText = enc == Fmt.Dash && dec == Fmt.Dash ? "" : $"编解码  {enc}% / {dec}%";

        // ---------------- 内存 ----------------
        double? used = Value(snap, MetricId.RamUsedMb);
        double? total = Value(snap, MetricId.RamTotalMb);
        double? load = Value(snap, MetricId.RamLoad);

        MemHeadline = used.HasValue && total.HasValue
            ? $"{used.Value / 1024:F1} / {total.Value / 1024:F1} GB"
            : "读取中…";

        if (load.HasValue) MemLoad.Apply(Fmt.Pct(load.Value), Severity.Load(load.Value), bar: load.Value);
        else MemLoad.Unavailable();

        double? commitUsed = Value(snap, MetricId.RamCommitUsedMb);
        double? commitLimit = Value(snap, MetricId.RamCommitLimitMb);
        MemUsed.Apply(used.HasValue ? (used.Value / 1024).ToString("F1") : Fmt.Dash, Level.Normal);
        MemCommit.Apply(commitUsed.HasValue ? (commitUsed.Value / 1024).ToString("F1") : Fmt.Dash,
                        Level.Normal,
                        commitLimit.HasValue ? $"/ 上限 {commitLimit.Value / 1024:F0} GB" : null);
        double? cached = Value(snap, MetricId.RamCachedMb);
        MemCached.Apply(cached.HasValue ? (cached.Value / 1024).ToString("F1") : Fmt.Dash, Level.Normal);

        double? pageFile = Value(snap, MetricId.RamPageFileMb);
        MemDetail = pageFile.HasValue ? $"页面文件 ~{pageFile.Value / 1024:F1} GB（推算值）" : "";

        // ---------------- 硬盘 ----------------
        bool hasTemp = snap.TryGetSeries(MetricId.DiskTemperature, out var temps) && temps.IsUsable;
        foreach (var row in Disks)
        {
            if (hasTemp && row.Index >= 0 && row.Index < temps.Values.Length
                && !double.IsNaN(temps.Values[row.Index]))
                row.SetTemp(temps.Values[row.Index]);
            else
                row.ClearTemp();
        }

        // 5.3 要求每盘一行带 D03，但采集层目前只有 PhysicalDisk(_Total) 的合计值（R21）。
        // 这里如实标"合计"，不把它拆到每一行假装是单盘读数。
        string read = TryText(snap, MetricId.DiskReadBytesPerSec, Fmt.Rate);
        string write = TryText(snap, MetricId.DiskWriteBytesPerSec, Fmt.Rate);
        DiskSummary = read == Fmt.Dash && write == Fmt.Dash
            ? (hasTemp ? "" : "硬盘温度需管理员权限")
            : $"合计  读 {read} MB/s   写 {write} MB/s";
    }

    // ---------------- 内部小工具 ----------------

    private static string FormatLoadAvg(MetricSnapshot snap)
    {
        string[] parts =
        {
            TryText(snap, MetricId.CpuLoadAvg1, v => $"{v:F2}"),
            TryText(snap, MetricId.CpuLoadAvg5, v => $"{v:F2}"),
            TryText(snap, MetricId.CpuLoadAvg15, v => $"{v:F2}")
        };
        return parts.All(p => p == Fmt.Dash) ? Fmt.Dash : string.Join(" / ", parts);
    }

    private static double? Value(MetricSnapshot snap, MetricId id)
        => snap.TryGet(id, out var s) && s.IsUsable ? s.Value : null;

    private static string TryText(MetricSnapshot snap, MetricId id, Func<double, string> fmt)
        => snap.TryGet(id, out var s) && s.IsUsable ? fmt(s.Value) : Fmt.Dash;

    private static void SetCell(MetricCell cell, MetricSnapshot snap, MetricId id,
                                Func<double, string> fmt,
                                Func<double, Level>? severity = null,
                                bool withBar = false)
    {
        if (snap.TryGet(id, out var s) && s.IsUsable)
            cell.Apply(fmt(s.Value), severity?.Invoke(s.Value) ?? Level.Normal,
                       bar: withBar ? s.Value : null);
        else
            cell.Unavailable();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
