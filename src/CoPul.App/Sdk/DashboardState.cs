using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;

namespace CoPul.App.Sdk;

/// <summary>硬盘区块的一行。型号 / 类型 / 容量来自 <see cref="SystemInfo"/>，温度与速率每次快照刷新。</summary>
public sealed class DiskRow : INotifyPropertyChanged
{
    private string _temp = Fmt.Dash;
    private Level _level = Level.Muted;
    private string _readText = Fmt.Dash;
    private string _writeText = Fmt.Dash;

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

    /// <summary>D03 单盘读速率（MB/s）。与合计分开显示，别把合计值摊到每行假装是单盘读数。</summary>
    public string ReadText => _readText;
    public string WriteText => _writeText;

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

    /// <summary>
    /// 单盘速率。<paramref name="readBps"/> / <paramref name="writeBps"/> 为 NaN 表示这块盘
    /// 没有挂上 PDH 实例（例如热插拔后才出现，见 R23），显示 `—` 而不是 0。
    /// </summary>
    public void SetRates(double readBps, double writeBps)
    {
        string r = double.IsNaN(readBps) ? Fmt.Dash : Fmt.Rate(readBps);
        string w = double.IsNaN(writeBps) ? Fmt.Dash : Fmt.Rate(writeBps);
        if (_readText != r) { _readText = r; Raise(nameof(ReadText)); }
        if (_writeText != w) { _writeText = w; Raise(nameof(WriteText)); }
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 进程表里的一行（M1.4）。
///
/// 复用 <see cref="DiskRow"/> 的套路：行对象常驻、只改字段，不整表重建。
/// 进程每 5 秒刷新一次，若每次都清空 ObservableCollection 再 Add，WPF 会销毁重建
/// 全部视觉元素，行高、选中态、滚动位置都会跳。
/// </summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _pidText = "";
    private string _cpuText = Fmt.Dash;
    private string _gpuText = Fmt.Dash;
    private string _memText = Fmt.Dash;
    private Level _cpuLevel = Level.Muted;

    public string Name { get => _name; private set => SetStr(ref _name, value); }
    public string PidText { get => _pidText; private set => SetStr(ref _pidText, value); }

    /// <summary>CPU 占用率。首次采样算不出差分时显示 `—`，不写 0（A-13）。</summary>
    public string CpuText { get => _cpuText; private set => SetStr(ref _cpuText, value); }

    /// <summary>GPU 占用率。WDDM 显卡拿不到每进程 GPU 数据时整列是 `—`。</summary>
    public string GpuText { get => _gpuText; private set => SetStr(ref _gpuText, value); }

    public string MemText { get => _memText; private set => SetStr(ref _memText, value); }

    public Brush CpuBrush => Palette.Brush(_cpuLevel switch
    {
        Level.Warn => "BrushWarn",
        Level.Danger => "BrushDanger",
        Level.Muted => "BrushTextSecondary",
        _ => "AccentCpu"
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ProcessStat s)
    {
        Name = s.Name.Length > 0 ? s.Name : "(未知)";
        PidText = s.Pid.ToString(CultureInfo.InvariantCulture);

        if (double.IsNaN(s.CpuPct))
        {
            CpuText = Fmt.Dash;
            SetLevel(Level.Muted);
        }
        else
        {
            // 占用率低于 10% 时给一位小数，否则整数 —— 0.3% 与 0% 的差别要看得出
            CpuText = s.CpuPct < 10
                ? s.CpuPct.ToString("F1", CultureInfo.InvariantCulture)
                : s.CpuPct.ToString("F0", CultureInfo.InvariantCulture);
            SetLevel(Severity.Load(s.CpuPct));
        }

        GpuText = double.IsNaN(s.GpuPct)
            ? Fmt.Dash
            : s.GpuPct.ToString("F0", CultureInfo.InvariantCulture);

        MemText = s.MemMb >= 1024
            ? $"{s.MemMb / 1024:F1} GB"
            : $"{s.MemMb:F0} MB";
    }

    private void SetLevel(Level level)
    {
        if (_cpuLevel == level) return;
        _cpuLevel = level;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CpuBrush)));
    }

    private void SetStr(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
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
/// 一个可折叠区块的状态（5.4「各区块标题左侧有折叠箭头，状态持久化」）。
///
/// 折叠不只是把内容藏起来：<see cref="DashboardState.ApplySnapshot"/> 会跳过折叠区块的
/// 文案计算，所以折叠是真的省 CPU——尤其是 CPU 区块那行 20 个核心的字符串拼接。
/// </summary>
public sealed class BlockState : INotifyPropertyChanged
{
    private bool _collapsed;

    public BlockState(string id) => Id = id;

    /// <summary>持久化用的 id：cpu / gpu / mem / disk。</summary>
    public string Id { get; }

    public bool Collapsed
    {
        get => _collapsed;
        private set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            Raise(nameof(Collapsed));
            Raise(nameof(BodyVisibility));
            Raise(nameof(Arrow));
            Raise(nameof(HeaderMargin));
        }
    }

    public Visibility BodyVisibility => _collapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>折叠箭头字形。用文字而不是 Path：少一个视觉资源，且在等宽字体下对齐稳定。</summary>
    public string Arrow => _collapsed ? "▸" : "▾";

    /// <summary>折叠后标题行不再需要下边距（正文没了，留白会显得空）。</summary>
    public Thickness HeaderMargin => _collapsed ? new Thickness(0) : new Thickness(0, 12, 0, 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>恢复到配置里的值。不触发 <see cref="Changed"/>，避免启动时把默认值写回磁盘。</summary>
    public void Restore(bool collapsed) => Collapsed = collapsed;

    /// <summary>用户点击。触发 <see cref="Changed"/> 让窗口去持久化。</summary>
    public void Toggle() => Collapsed = !Collapsed;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    private string _netAdapter = "读取中…";
    private string _netTargetText = "";
    private string _procSubtitle = "";
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
        // 风扇格已移除：移动版 GPU 的风扇归笔记本 EC/BIOS 管，NVML 不暴露转速
        // （nvidia-smi 同样报 N/A），永远不会出数，见 R26。

        MemLoad = new MetricCell("占用率", "%", "AccentRam");
        MemUsed = new MetricCell("已用", "GB", "AccentRam");
        MemCommit = new MetricCell("提交", "GB", "AccentRam");
        MemCached = new MetricCell("已缓存", "GB", "AccentRam");

        // 网络四项：速率单位由 Fmt.RateAuto 在 KB/s 与 MB/s 间自适应，构造时给个初始值即可
        NetDown = new MetricCell("下行", "MB/s", "AccentNet");
        NetUp = new MetricCell("上行", "MB/s", "AccentNet");
        NetLatency = new MetricCell("延迟", "ms", "AccentNet");
        NetLoss = new MetricCell("丢包率", "%", "AccentNet");
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

    public MetricCell MemLoad { get; }
    public MetricCell MemUsed { get; }
    public MetricCell MemCommit { get; }
    public MetricCell MemCached { get; }

    public MetricCell NetDown { get; }
    public MetricCell NetUp { get; }
    public MetricCell NetLatency { get; }
    public MetricCell NetLoss { get; }

    public ObservableCollection<DiskRow> Disks { get; } = new();

    /// <summary>进程表行。行对象复用，只改字段。</summary>
    public ObservableCollection<ProcessRow> Processes { get; } = new();

    /// <summary>CPU / GPU 使用率历史。120 点 × 0.5s = 近 60 秒。</summary>
    public TrendSeries Trend { get; } = new(120);

    // ---- 折叠状态（5.4，持久化到 config.json） ----
    public BlockState CpuBlock { get; } = new("cpu");
    public BlockState GpuBlock { get; } = new("gpu");
    public BlockState MemBlock { get; } = new("mem");
    public BlockState NetBlock { get; } = new("net");
    public BlockState DiskBlock { get; } = new("disk");
    public BlockState TrendBlock { get; } = new("trend");
    public BlockState ProcBlock { get; } = new("proc");

    public IEnumerable<BlockState> Blocks => new[]
    {
        CpuBlock, GpuBlock, MemBlock, NetBlock, DiskBlock, TrendBlock, ProcBlock
    };

    /// <summary>按 id 找区块。恢复配置时用，避免开一串 switch。</summary>
    public BlockState? Block(string id) => Blocks.FirstOrDefault(b => b.Id == id);

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

    /// <summary>N05 活动网卡名。字符串指标不在 MetricSnapshot（那里只装 double），由 CollectorHost 旁路传入。</summary>
    public string NetAdapter { get => _netAdapter; private set => Set(ref _netAdapter, value); }

    /// <summary>延迟探测目标，让「延迟」这一格知道自己在测谁。</summary>
    public string NetTargetText { get => _netTargetText; private set => Set(ref _netTargetText, value); }

    /// <summary>进程区块右侧的说明：刷新节奏 + 每进程 GPU 在这台机器上能不能拿到。</summary>
    public string ProcSubtitle { get => _procSubtitle; private set => Set(ref _procSubtitle, value); }

    /// <summary>趋势图副标题。写明窗口长度与量程，省掉纵轴刻度文字。</summary>
    public string TrendSubtitle { get; } = "近 60 秒 · 0–100%";

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

    /// <summary>静态信息到货：型号、盘列表、分区。D04 之后每 10s 由 <see cref="ApplyPartitions"/> 刷新。</summary>
    public void ApplyInfo(SystemInfo info)
    {
        CpuModel = info.CpuName.Length > 0 ? info.CpuName : "未知 CPU";

        GpuModel = info.GpuName.Length > 0
            ? info.GpuName
            : "未知 GPU";

        Disks.Clear();
        foreach (var d in info.Disks)
            Disks.Add(new DiskRow(d.Index, d.Model, d.Bus, d.CapacityGb));

        PartitionText = FormatPartitions(info.Partitions);

        StatusText = info.Warnings.Count == 0 ? "采集正常" : string.Join(" · ", info.Warnings);
        _infoNote = info.Warnings.Count == 0 ? "" : string.Join(" · ", info.Warnings);
    }

    /// <summary>D04 10 秒刷新（4.4）。只改分区这一行，不碰其他区块。</summary>
    public void ApplyPartitions(IReadOnlyList<PartitionInfo> parts)
        => PartitionText = FormatPartitions(parts);

    private static string FormatPartitions(IReadOnlyList<PartitionInfo> parts)
        => parts.Count == 0
            ? "分区信息不可用"
            : string.Join("    ", parts.Select(p =>
                $"{p.Name}  {p.UsedPercent:F0}%  ({p.TotalMb / 1024 - p.FreeMb / 1024:F0}/{p.TotalMb / 1024:F0} GB)"));

    /// <summary>自测用：跳过「各核心」长文本的更新，用来判断布局成本是否集中在它上面。</summary>
    public bool SuppressCoreLine { get; set; }

    /// <summary>每帧刷新（0.5s 一次），只做字符串格式化与赋值。折叠的区块整段跳过。</summary>
    public void ApplySnapshot(MetricSnapshot snap)
    {
        if (!CpuBlock.Collapsed) ApplyCpu(snap);
        if (!GpuBlock.Collapsed) ApplyGpu(snap);
        if (!MemBlock.Collapsed) ApplyMemory(snap);
        if (!NetBlock.Collapsed) ApplyNetwork(snap);
        if (!DiskBlock.Collapsed) ApplyDisk(snap);
    }

    /// <summary>
    /// N05 网卡名与探测目标。两者是字符串，<see cref="MetricSnapshot"/> 装不下（只存 double），
    /// 所以由 CollectorHost 每帧旁路带过来。无变化时 <see cref="Set"/> 直接返回，没有额外绑定刷新。
    /// </summary>
    public void ApplyNetworkContext(string adapter, string target)
    {
        // 空表示还没采到（PDH 首次 Collect 前），不能断言"没有网卡"，所以显示 `—`
        NetAdapter = string.IsNullOrWhiteSpace(adapter) ? Fmt.Dash : adapter;
        NetTargetText = string.IsNullOrWhiteSpace(target) ? "" : $"探测 {target}";
    }

    /// <summary>
    /// 进程表（M1.4）。行对象复用：多出来的行删掉、缺的行补上，已有的行只改字段，
    /// 避免每 5 秒把整张表的视觉元素重建一遍。
    /// </summary>
    public void ApplyProcesses(IReadOnlyList<ProcessStat> top, bool gpuAvailable)
    {
        ProcSubtitle = gpuAvailable
            ? "每 5 秒刷新 · 按占用排序"
            : "每 5 秒刷新 · 本机不提供每进程 GPU";

        while (Processes.Count > top.Count) Processes.RemoveAt(Processes.Count - 1);
        while (Processes.Count < top.Count) Processes.Add(new ProcessRow());

        for (int i = 0; i < top.Count; i++)
            Processes[i].Apply(top[i]);
    }

    /// <summary>
    /// 往趋势图追加一个采样点。折叠时由调用方跳过 —— 看不见的历史没有价值。
    /// 读不到的项写 NaN，画图时断开，绝不写 0（A-13）。
    /// </summary>
    public void PushTrend(MetricSnapshot snap)
        => Trend.Push(Value(snap, MetricId.CpuTotalLoad) ?? double.NaN,
                      Value(snap, MetricId.GpuLoad) ?? double.NaN);

    private void ApplyCpu(MetricSnapshot snap)
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
    }

    private void ApplyGpu(MetricSnapshot snap)
    {
        // ---------------- GPU ----------------
        SetCell(GpuLoad, snap, MetricId.GpuLoad, Fmt.Pct, Severity.Load, withBar: true);
        SetCell(GpuTemp, snap, MetricId.GpuTemp, Fmt.Temp, Severity.Temp);
        SetCell(GpuPower, snap, MetricId.GpuPowerW, Fmt.Watts);
        SetCell(GpuClock, snap, MetricId.GpuClockCoreMhz, Fmt.Mhz);

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
    }

    private void ApplyMemory(MetricSnapshot snap)
    {
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
    }

    private void ApplyNetwork(MetricSnapshot snap)
    {
        // ---------------- 网络 ----------------
        // 速率单位自适应（4.5：KB/s 或 MB/s，1 位小数）。没有数据时单位保持上次的值，
        // 值显示 `—` —— 不能因为读不到就先把单位改成 MB/s，那会让下次出数时闪一下。
        SetRateCell(NetDown, snap, MetricId.NetDownBytesPerSec);
        SetRateCell(NetUp, snap, MetricId.NetUpBytesPerSec);

        SetCell(NetLatency, snap, MetricId.NetLatencyMs, Fmt.Pct);

        // N04 丢包率：滚动 10 次探测的成功率。0% 是正常值，不是"无数据"，
        // 所以有样本就正常显示；样本不足一个窗口时也显示，只是波动大。
        double? loss = Value(snap, MetricId.NetPacketLossPercent);
        if (loss.HasValue)
        {
            Level level = loss.Value > 5 ? Level.Danger
                : loss.Value > 0 ? Level.Warn
                : Level.Normal;
            NetLoss.Apply(loss.Value.ToString("F0"), level);
        }
        else
        {
            NetLoss.Unavailable();
        }
    }

    /// <summary>速率格：数值与单位一起换，避免出现"1024.0 KB/s"这种跨量级读数。</summary>
    private static void SetRateCell(MetricCell cell, MetricSnapshot snap, MetricId id)
    {
        if (!snap.TryGet(id, out var s) || !s.IsUsable)
        {
            cell.Unavailable();
            return;
        }

        string text = Fmt.RateAuto(s.Value, out string unit);
        cell.SetUnit(unit);
        cell.Apply(text, Level.Normal);
    }

    private void ApplyDisk(MetricSnapshot snap)
    {
        // ---------------- 硬盘 ----------------
        bool hasTemp = snap.TryGetSeries(MetricId.DiskTemperature, out var temps) && temps.IsUsable;
        bool hasRead = snap.TryGetSeries(MetricId.DiskReadPerDisk, out var reads) && reads.IsUsable;
        bool hasWrite = snap.TryGetSeries(MetricId.DiskWritePerDisk, out var writes) && writes.IsUsable;

        foreach (var row in Disks)
        {
            int i = row.Index;
            if (hasTemp && i >= 0 && i < temps.Values.Length && !double.IsNaN(temps.Values[i]))
                row.SetTemp(temps.Values[i]);
            else
                row.ClearTemp();

            double r = hasRead && i >= 0 && i < reads.Values.Length ? reads.Values[i] : double.NaN;
            double w = hasWrite && i >= 0 && i < writes.Values.Length ? writes.Values[i] : double.NaN;
            row.SetRates(r, w);
        }

        // 合计单独一行，与每盘读数并列。5.3 要求"每盘一行带 D03"，不把合计摊到每行。
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
